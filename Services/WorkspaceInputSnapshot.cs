using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotSight.Services;

internal sealed class WorkspaceInputSnapshot
{
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison FilePathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
        ".csproj",
        ".fsproj",
        ".props",
        ".targets",
        ".proj",
        ".sln",
        ".slnx",
        ".tasks",
        ".build",
        ".vbproj",
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
    private static readonly HashSet<string> InitialComparisonSkippedDirectories =
        new(SkippedDirectories, StringComparer.OrdinalIgnoreCase)
        {
            "obj",
        };

    private static readonly string[] CommonBuildInputs =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config",
        "nuget.config",
        ".editorconfig",
        ".globalconfig",
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
            .Distinct(FilePathComparer)
            .ToArray();
        _files = CaptureFiles(_roots, _knownInputs);
    }

    public int FileCount => _files.Count;

    public static WorkspaceInputSnapshot Create(Solution solution, string entryPath)
    {
        var roots = new HashSet<string>(FilePathComparer);
        var inputs = new HashSet<string>(FilePathComparer)
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

            AddTextDocumentPaths(project.Documents, inputs, roots);
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

    public static WorkspaceInputSnapshot Create(string entryPath)
    {
        var inputs = new HashSet<string>(FilePathComparer)
        {
            entryPath,
        };
        var entryDirectory = Path.GetDirectoryName(entryPath);
        if (entryDirectory is null)
            return new WorkspaceInputSnapshot([], inputs);

        AddAncestorBuildInputs(entryDirectory, inputs);
        var extension = Path.GetExtension(entryPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            inputs.Add(Path.Combine(entryDirectory, "obj", "project.assets.json"));
        }
        return new WorkspaceInputSnapshot([entryDirectory], inputs);
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

    public bool HasComparableReloadInputsChanged()
    {
        var expected = GetComparableReloadInputStamps(_files);
        var current = GetComparableReloadInputStamps(CaptureFiles(_roots, _knownInputs));
        if (current.Count != expected.Count)
            return true;

        return expected.Any(item =>
            !current.TryGetValue(item.Key, out var currentStamp)
            || currentStamp != item.Value);
    }

    public bool HasSameReloadInputs(WorkspaceInputSnapshot other)
    {
        if (GetNewRoots(other).Count > 0)
        {
            return false;
        }

        var inputs = GetComparableReloadInputs(_files);
        var otherInputs = GetComparableReloadInputs(other._files);
        return inputs.SetEquals(otherInputs);
    }

    internal IReadOnlyList<string> GetNewReloadInputs(WorkspaceInputSnapshot other)
    {
        var differences = GetNewRoots(other)
            .Select(root => $"root:{root}")
            .ToList();
        differences.AddRange(
            GetComparableReloadInputs(other._files)
                .Except(GetComparableReloadInputs(_files), FilePathComparer)
                .Select(path => $"input:{path}"));
        return differences;
    }

    private static void AddTextDocumentPaths<TDocument>(
        IEnumerable<TDocument> documents,
        HashSet<string> inputs,
        HashSet<string>? roots = null)
        where TDocument : TextDocument
    {
        foreach (var document in documents)
        {
            if (document.FilePath is not null)
            {
                inputs.Add(document.FilePath);
                var directory = Path.GetDirectoryName(document.FilePath);
                if (directory is not null && !IsInSkippedDirectory(directory))
                    roots?.Add(directory);
            }
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
            .Distinct(FilePathComparer)
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
        var files = new Dictionary<string, FileStamp>(FilePathComparer);

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

    private static bool IsInSkippedDirectory(string path)
    {
        return IsInNamedDirectory(path, SkippedDirectories);
    }

    private static bool IsInInitialComparisonSkippedDirectory(string path)
    {
        return IsInNamedDirectory(path, InitialComparisonSkippedDirectories);
    }

    private static bool IsInNamedDirectory(string path, HashSet<string> directoryNames)
    {
        for (var directory = new DirectoryInfo(path);
             directory is not null;
             directory = directory.Parent)
        {
            if (directoryNames.Contains(directory.Name))
                return true;
        }

        return false;
    }

    // MSBuild-only text and intermediate outputs are validated by the post-open snapshot.
    private bool IsComparableReloadInput(string path) =>
        !IsInInitialComparisonSkippedDirectory(Path.GetDirectoryName(path) ?? path)
        && (CommonBuildInputs.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            || (_roots.Any(root => IsPathWithin(path, root))
                && IsRelevantFile(path)));

    private List<string> GetNewRoots(WorkspaceInputSnapshot other) =>
        other._roots
            .Where(otherRoot => !_roots.Any(root => IsPathWithin(otherRoot, root)))
            .ToList();

    private HashSet<string> GetComparableReloadInputs(
        IEnumerable<KeyValuePair<string, FileStamp>> files) =>
        files
            .Where(item => item.Value.Exists && IsComparableReloadInput(item.Key))
            .Select(item => item.Key)
            .ToHashSet(FilePathComparer);

    private Dictionary<string, FileStamp> GetComparableReloadInputStamps(
        IEnumerable<KeyValuePair<string, FileStamp>> files) =>
        files
            .Where(item => IsComparableReloadInput(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, FilePathComparer);

    private static bool IsPathWithin(string candidate, string root)
    {
        if (string.Equals(candidate, root, FilePathComparison))
            return true;

        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, FilePathComparison);
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
