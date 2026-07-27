using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotSight.Services;

internal sealed class WorkspaceInputSnapshot
{
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
        ".props",
        ".targets",
        ".proj",
        ".tasks",
        ".build",
        ".xml",
        ".editorconfig",
        ".globalconfig",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".idea",
        ".vs",
        "bin",
        "node_modules",
        "packages",
    };

    private static readonly string[] CommonBuildInputs =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config",
        "nuget.config",
    ];

    private readonly string[] _roots;
    private readonly string[] _knownInputs;
    private readonly Dictionary<string, FileStamp> _files;

    private WorkspaceInputSnapshot(IEnumerable<string> roots, IEnumerable<string> knownInputs)
    {
        _roots = NormalizeRoots(roots);
        _knownInputs = knownInputs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _files = CaptureFiles(_roots, _knownInputs);
    }

    public int FileCount => _files.Count;

    public static WorkspaceInputSnapshot Create(Solution solution, string entryPath)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entryPath,
        };

        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not null)
            {
                inputs.Add(project.FilePath);
                var projectDirectory = Path.GetDirectoryName(project.FilePath);
                if (projectDirectory is not null)
                {
                    roots.Add(projectDirectory);
                    inputs.Add(Path.Combine(projectDirectory, "obj", "project.assets.json"));
                    AddAncestorBuildInputs(projectDirectory, inputs);
                }
            }

            AddTextDocumentPaths(project.Documents, inputs);
            AddTextDocumentPaths(project.AdditionalDocuments, inputs);
            AddTextDocumentPaths(project.AnalyzerConfigDocuments, inputs);

            foreach (var metadataReference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                if (metadataReference.FilePath is not null)
                    inputs.Add(metadataReference.FilePath);
            }

            foreach (var analyzerReference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
                inputs.Add(analyzerReference.FullPath);
        }

        var entryDirectory = Path.GetDirectoryName(entryPath);
        if (entryDirectory is not null)
        {
            roots.Add(entryDirectory);
            AddAncestorBuildInputs(entryDirectory, inputs);
        }

        return new WorkspaceInputSnapshot(roots, inputs);
    }

    internal static WorkspaceInputSnapshot CreateForPaths(
        IEnumerable<string> roots,
        IEnumerable<string> knownInputs) =>
        new(roots, knownInputs);

    public bool HasChanged()
    {
        var current = CaptureFiles(_roots, _knownInputs);
        if (current.Count != _files.Count)
            return true;

        foreach (var (path, stamp) in _files)
        {
            if (!current.TryGetValue(path, out var currentStamp) || currentStamp != stamp)
                return true;
        }

        return false;
    }

    public bool HasSameTrackedInputs(WorkspaceInputSnapshot other) =>
        _files.Count == other._files.Count
        && _files.Keys.All(other._files.ContainsKey);

    private static void AddTextDocumentPaths<TDocument>(
        IEnumerable<TDocument> documents,
        HashSet<string> inputs)
        where TDocument : TextDocument
    {
        foreach (var document in documents)
        {
            if (document.FilePath is not null)
                inputs.Add(document.FilePath);
        }
    }

    private static void AddAncestorBuildInputs(string startDirectory, HashSet<string> inputs)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            foreach (var fileName in CommonBuildInputs)
                inputs.Add(Path.Combine(directory.FullName, fileName));
            directory = directory.Parent;
        }
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots)
    {
        var normalized = roots
            .Where(Directory.Exists)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();

        var minimal = new List<string>();
        foreach (var candidate in normalized)
        {
            if (!minimal.Any(root => IsPathWithin(candidate, root)))
                minimal.Add(candidate);
        }

        return minimal.ToArray();
    }

    private static Dictionary<string, FileStamp> CaptureFiles(
        IEnumerable<string> roots,
        IEnumerable<string> knownInputs)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in knownInputs)
            files[input] = FileStamp.Create(input);

        foreach (var root in roots)
        {
            foreach (var path in EnumerateRelevantFiles(root))
                files[path] = FileStamp.Create(path);
        }

        return files;
    }

    private static IEnumerable<string> EnumerateRelevantFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (SkippedDirectories.Contains(Path.GetFileName(entry)))
                        continue;

                    pending.Push(entry);
                    continue;
                }

                if (IsRelevantFile(entry))
                    yield return NormalizePath(entry);
            }
        }
    }

    private static bool IsRelevantFile(string path)
    {
        if (RelevantExtensions.Contains(Path.GetExtension(path)))
            return true;

        return string.Equals(
            Path.GetFileName(path),
            "project.assets.json",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private readonly record struct FileStamp(bool Exists, long Length, long LastWriteUtcTicks)
    {
        public static FileStamp Create(string path)
        {
            var file = new FileInfo(path);
            return file.Exists
                ? new FileStamp(true, file.Length, file.LastWriteTimeUtc.Ticks)
                : new FileStamp(false, 0, 0);
        }
    }
}
