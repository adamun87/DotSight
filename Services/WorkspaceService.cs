using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotSight.Services;

public sealed class WorkspaceService : IDisposable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoadedWorkspace? _current;
    private long _snapshotVersion;
    private McpServer? _server;

    public WorkspaceService(WorkspaceOptions options, ILogger<WorkspaceService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Sets the MCP server reference for roots-based workspace discovery.
    /// Called once from tool invocations that have the server injected.
    /// </summary>
    public void SetServer(McpServer server) => _server ??= server;

    /// <summary>
    /// Acquires a request-owned workspace snapshot. If <paramref name="solution"/> is specified,
    /// it overrides any previously loaded solution. Accepts a .sln, .slnx, or .csproj
    /// filename (resolved relative to workspace root) or an absolute path.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(
        string? solution = null,
        CancellationToken ct = default)
    {
        var requestedPath = await ResolveSolutionArgAsync(solution, ct);
        var reusable = TryAcquireReusableSnapshot(Volatile.Read(ref _current), requestedPath);
        if (reusable is not null)
            return reusable;

        await _gate.WaitAsync(ct);
        try
        {
            var current = Volatile.Read(ref _current);
            reusable = TryAcquireReusableSnapshot(current, requestedPath);
            if (reusable is not null)
                return reusable;

            var pathToLoad = requestedPath
                ?? current?.ResolvedPath
                ?? await DiscoverSolutionPathAsync(ct);
            if (current is not null
                && !string.Equals(pathToLoad, current.ResolvedPath, PathComparison))
            {
                _logger.LogInformation("Switching solution to: {Path}", pathToLoad);
            }
            else if (current is not null)
            {
                _logger.LogInformation("Saved source or build inputs changed on disk, reloading solution");
            }

            var loaded = await LoadSolutionCoreAsync(pathToLoad, current, ct);
            return loaded.TryAcquire()
                ?? throw new InvalidOperationException("The newly loaded workspace was retired before use.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkspaceSnapshot? TryAcquireReusableSnapshot(
        LoadedWorkspace? current,
        string? requestedPath)
    {
        if (current is null)
            return null;
        if (requestedPath is not null
            && !string.Equals(requestedPath, current.ResolvedPath, PathComparison))
        {
            return null;
        }
        if (current.InputSnapshot.HasChanged())
            return null;

        return current.TryAcquire();
    }

    private async Task<LoadedWorkspace> LoadSolutionCoreAsync(
        string path,
        LoadedWorkspace? current,
        CancellationToken ct)
    {
        const int maxLoadAttempts = 3;
        var isReloadingCurrentPath = current is not null
            && string.Equals(path, current.ResolvedPath, PathComparison);
        var loadBaseline = isReloadingCurrentPath
            ? WorkspaceInputSnapshot.Create(current!.Solution, path)
            : WorkspaceInputSnapshot.Create(path);
        var hasDiscoveredLoadBaseline = isReloadingCurrentPath;

        for (var attempt = 1; attempt <= maxLoadAttempts; attempt++)
        {
            _logger.LogInformation(
                "Opening: {Path} (attempt {Attempt}/{MaxAttempts})",
                path,
                attempt,
                maxLoadAttempts);
            MSBuildWorkspace? candidateWorkspace = MSBuildWorkspace.Create();
            string? candidateShadowCopyDir = null;

            try
            {
                candidateWorkspace.RegisterWorkspaceFailedHandler(e =>
                    _logger.LogWarning("Workspace warning: {Message}", e.Diagnostic.Message));

                var ext = Path.GetExtension(path);
                Solution candidateSolution;
                if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
                {
                    var project = await candidateWorkspace.OpenProjectAsync(path, cancellationToken: ct);
                    candidateSolution = project.Solution;
                }
                else
                {
                    candidateSolution = await candidateWorkspace.OpenSolutionAsync(
                        path,
                        cancellationToken: ct);
                }

                var discoveredSnapshot = WorkspaceInputSnapshot.Create(candidateSolution, path);

                // Retry only when loading discovers mutable inputs outside the pre-open baseline.
                // Binary references are checked by the published snapshot but do not force every
                // stable cold load to open the solution twice.
                if (!loadBaseline.HasSameReloadInputs(discoveredSnapshot))
                {
                    _logger.LogInformation(
                        "Workspace input scope expanded while opening {Path}; retrying once with the complete baseline. New inputs: {NewInputs}",
                        path,
                        string.Join(", ", loadBaseline.GetNewReloadInputs(discoveredSnapshot).Take(10)));
                    loadBaseline = discoveredSnapshot;
                    hasDiscoveredLoadBaseline = true;
                    continue;
                }

                var loadInputsChanged = hasDiscoveredLoadBaseline
                    ? loadBaseline.HasChanged()
                    : loadBaseline.HasComparableReloadInputsChanged();
                if (loadInputsChanged)
                {
                    _logger.LogWarning(
                        "Workspace inputs changed while loading {Path}; retrying with a fresh snapshot",
                        path);
                    loadBaseline = discoveredSnapshot;
                    hasDiscoveredLoadBaseline = true;
                    continue;
                }

                candidateSolution = await PinSolutionTextAsync(candidateSolution, ct);
                if (discoveredSnapshot.HasChanged())
                {
                    _logger.LogWarning(
                        "Workspace inputs changed while materializing {Path}; retrying with a fresh snapshot",
                        path);
                    loadBaseline = WorkspaceInputSnapshot.Create(candidateSolution, path);
                    hasDiscoveredLoadBaseline = true;
                    continue;
                }

                var candidateSnapshot = WorkspaceInputSnapshot.Create(candidateSolution, path);
                if (candidateSnapshot.HasChanged())
                {
                    loadBaseline = candidateSnapshot;
                    hasDiscoveredLoadBaseline = true;
                    continue;
                }

                (candidateSolution, candidateShadowCopyDir) =
                    ShadowCopyAnalyzerReferences(candidateSolution);
                var info = new WorkspaceSnapshotInfo(
                    ++_snapshotVersion,
                    DateTime.UtcNow,
                    candidateSnapshot.FileCount,
                    "saved files");
                var loaded = new LoadedWorkspace(
                    candidateWorkspace,
                    candidateSolution,
                    path,
                    candidateSnapshot,
                    candidateShadowCopyDir,
                    info);
                var previous = Interlocked.Exchange(ref _current, loaded);
                candidateWorkspace = null;
                candidateShadowCopyDir = null;

                previous?.Retire();
                _logger.LogInformation(
                    "Loaded snapshot {Version}: {ProjectCount} projects, {InputCount} tracked inputs",
                    info.Version,
                    candidateSolution.ProjectIds.Count,
                    info.TrackedInputs);
                return loaded;
            }
            finally
            {
                candidateWorkspace?.Dispose();
                CleanupShadowDir(candidateShadowCopyDir);
            }
        }

        throw new InvalidOperationException(
            $"Workspace inputs kept changing while loading '{path}'. Retry after file writes settle.");
    }

    private static async Task<Solution> PinSolutionTextAsync(
        Solution solution,
        CancellationToken ct)
    {
        var documentTextsTask = Task.WhenAll(
            solution.Projects
                .SelectMany(project => project.Documents)
                .Select(async document => (
                    document.Id,
                    Text: await document.GetTextAsync(ct))));
        var additionalTextsTask = Task.WhenAll(
            solution.Projects
                .SelectMany(project => project.AdditionalDocuments)
                .Select(async document => (
                    document.Id,
                    Text: await document.GetTextAsync(ct))));
        var analyzerConfigTextsTask = Task.WhenAll(
            solution.Projects
                .SelectMany(project => project.AnalyzerConfigDocuments)
                .Select(async document => (
                    document.Id,
                    Text: await document.GetTextAsync(ct))));

        await Task.WhenAll(
            documentTextsTask,
            additionalTextsTask,
            analyzerConfigTextsTask);

        foreach (var (documentId, text) in await documentTextsTask)
        {
            solution = solution.WithDocumentText(
                documentId,
                text,
                PreservationMode.PreserveValue);
        }

        foreach (var (documentId, text) in await additionalTextsTask)
        {
            solution = solution.WithAdditionalDocumentText(
                documentId,
                text,
                PreservationMode.PreserveValue);
        }

        foreach (var (documentId, text) in await analyzerConfigTextsTask)
        {
            solution = solution.WithAnalyzerConfigDocumentText(
                documentId,
                text,
                PreservationMode.PreserveValue);
        }

        return solution;
    }

    /// <summary>
    /// Resolves a user-provided solution/project argument to an absolute path.
    /// Accepts .sln, .slnx, .csproj filenames, relative paths, or absolute paths.
    /// Returns null if no value was specified.
    /// </summary>
    private async Task<string?> ResolveSolutionArgAsync(string? solution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(solution))
            return null;

        // Already absolute
        if (Path.IsPathRooted(solution) && File.Exists(solution))
            return Path.GetFullPath(solution);

        // Try resolving relative to workspace roots
        var rootDirs = await GetWorkspaceRootDirsAsync(ct);
        foreach (var rootDir in rootDirs)
        {
            var candidate = Path.GetFullPath(Path.Combine(rootDir, solution));
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            $"'{solution}' not found. Provide a valid .sln, .slnx, or .csproj filename or path.");
    }

    /// <summary>
    /// Returns metadata for the currently published snapshot.
    /// Request handlers should prefer the immutable info on their acquired snapshot.
    /// </summary>
    public object GetSnapshotInfo()
    {
        var info = Volatile.Read(ref _current)?.Info;
        return new
        {
            version = info?.Version ?? 0,
            loadedAtUtc = info?.LoadedAtUtc ?? default,
            trackedInputs = info?.TrackedInputs ?? 0,
            source = info?.Source ?? "saved files",
        };
    }

    /// <summary>
    /// Resolves a symbol from a fully qualified name within a project's compilation.
    /// </summary>
    public static INamedTypeSymbol? ResolveType(Compilation compilation, string fullyQualifiedName)
    {
        return compilation.GetTypeByMetadataName(fullyQualifiedName);
    }

    /// <summary>
    /// Resolves a symbol by fully qualified name, searching all accessible types in a compilation.
    /// Supports member lookup with "TypeName.MemberName" syntax.
    /// </summary>
    public static ISymbol? ResolveMember(Compilation compilation, string fullyQualifiedTypeName, string memberName)
    {
        var type = ResolveType(compilation, fullyQualifiedTypeName);
        if (type is null) return null;
        var members = type.GetMembers(memberName);
        return members.Length == 1 ? members[0] : null;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _current, null)?.Retire();
    }

    /// <summary>
    /// Replaces analyzer/generator references with non-locking versions so that
    /// DotSight never holds file locks on DLLs in project bin/obj directories.
    /// On Windows: shadow-copies DLLs that are in writable locations (bin/obj).
    /// On Linux/macOS: uses LoadFromStream which doesn't lock files at all.
    /// DLLs in read-only locations (NuGet cache, dotnet runtime) are loaded directly
    /// since they're never overwritten by builds.
    /// </summary>
    private static (Solution Solution, string? ShadowCopyDirectory) ShadowCopyAnalyzerReferences(
        Solution solution)
    {
        if (!solution.Projects.SelectMany(project => project.AnalyzerReferences)
                .Any(reference => reference is AnalyzerFileReference))
        {
            return (solution, null);
        }

        IAnalyzerAssemblyLoader loader;
        string? shadowCopyDir = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            shadowCopyDir = Path.Combine(
                Path.GetTempPath(),
                "dotsight",
                "shadow",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shadowCopyDir);
            loader = new ShadowCopyAnalyzerLoader(shadowCopyDir);
        }
        else
        {
            loader = new StreamAnalyzerLoader();
        }

        foreach (var project in solution.Projects)
        {
            if (!project.AnalyzerReferences.Any(r => r is AnalyzerFileReference))
                continue;

            var newRefs = project.AnalyzerReferences
                .Select(r => r is AnalyzerFileReference afr
                    ? (AnalyzerReference)new AnalyzerFileReference(afr.FullPath, loader)
                    : r)
                .ToList();
            solution = solution.WithProjectAnalyzerReferences(project.Id, newRefs);
        }

        return (solution, shadowCopyDir);
    }

    private static void CleanupShadowDir(string? path)
    {
        if (path is null)
            return;

        try { Directory.Delete(path, true); } catch { }
    }

    private sealed class LoadedWorkspace
    {
        private readonly object _lifetimeGate = new();
        private int _referenceCount = 1;
        private bool _retired;
        private bool _disposed;

        public LoadedWorkspace(
            MSBuildWorkspace workspace,
            Solution solution,
            string resolvedPath,
            WorkspaceInputSnapshot inputSnapshot,
            string? shadowCopyDirectory,
            WorkspaceSnapshotInfo info)
        {
            Workspace = workspace;
            Solution = solution;
            ResolvedPath = resolvedPath;
            InputSnapshot = inputSnapshot;
            ShadowCopyDirectory = shadowCopyDirectory;
            Info = info;
        }

        public MSBuildWorkspace Workspace { get; }

        public Solution Solution { get; }

        public string ResolvedPath { get; }

        public WorkspaceInputSnapshot InputSnapshot { get; }

        public string? ShadowCopyDirectory { get; }

        public WorkspaceSnapshotInfo Info { get; }

        public WorkspaceSnapshot? TryAcquire()
        {
            lock (_lifetimeGate)
            {
                if (_retired)
                    return null;
                _referenceCount++;
            }

            return new WorkspaceSnapshot(Solution, Info, Release);
        }

        public void Retire()
        {
            var dispose = false;
            lock (_lifetimeGate)
            {
                if (_retired)
                    return;

                _retired = true;
                _referenceCount--;
                if (_referenceCount == 0)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
                DisposeResources();
        }

        private void Release()
        {
            var dispose = false;
            lock (_lifetimeGate)
            {
                if (_referenceCount <= 0)
                    throw new InvalidOperationException("Workspace snapshot released more than once.");

                _referenceCount--;
                if (_retired && _referenceCount == 0)
                {
                    if (_disposed)
                        throw new InvalidOperationException("Workspace generation was already disposed.");
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
                DisposeResources();
        }

        private void DisposeResources()
        {
            Workspace.Dispose();
            CleanupShadowDir(ShadowCopyDirectory);
        }
    }

    /// <summary>
    /// Determines whether a DLL path is in a read-only location (NuGet cache, dotnet runtime,
    /// Program Files) where files are never overwritten by builds — no shadow copy needed.
    /// </summary>
    private static bool IsReadOnlyLocation(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');

        // NuGet package cache — packages are immutable once restored
        if (normalized.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase))
            return true;

        // .NET SDK/runtime directories — never modified by user builds
        var dotnetRoot = Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory())?.Replace('\\', '/');
        if (dotnetRoot is not null && normalized.StartsWith(dotnetRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        // Program Files on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).Replace('\\', '/');
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).Replace('\\', '/');
            if ((!string.IsNullOrEmpty(pf) && normalized.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(pf86) && normalized.StartsWith(pf86, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Windows: shadow-copies writable DLLs to a temp directory, loads read-only ones directly.
    /// </summary>
    private sealed class ShadowCopyAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        private readonly string _shadowDir;

        public ShadowCopyAnalyzerLoader(string shadowDir) => _shadowDir = shadowDir;

        public void AddDependencyLocation(string fullPath)
        {
            if (!File.Exists(fullPath) || IsReadOnlyLocation(fullPath))
                return;

            var dest = Path.Combine(_shadowDir, Path.GetFileName(fullPath));
            if (!File.Exists(dest))
                try { File.Copy(fullPath, dest); } catch { }
        }

        public Assembly LoadFromPath(string fullPath)
        {
            // Read-only locations: load directly — no locking concern
            if (IsReadOnlyLocation(fullPath))
                return Assembly.LoadFrom(fullPath);

            // Writable locations (bin/obj): shadow copy first
            var dest = Path.Combine(_shadowDir, Path.GetFileName(fullPath));
            if (!File.Exists(dest))
                File.Copy(fullPath, dest);
            return Assembly.LoadFrom(dest);
        }
    }

    /// <summary>
    /// Non-Windows: loads assemblies from a stream so the file is never locked.
    /// </summary>
    private sealed class StreamAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath)
        {
            var bytes = File.ReadAllBytes(fullPath);
            return Assembly.Load(bytes);
        }
    }

    private async Task<string> DiscoverSolutionPathAsync(CancellationToken ct)
    {
        // If --solution was passed at startup, use it directly
        if (_options.SolutionPath is not null)
            return _options.SolutionPath;

        // Try MCP roots first — the client (VS Code) knows the workspace folders
        var rootDirs = await GetWorkspaceRootDirsAsync(ct);
        var allSlnFiles = new List<string>();

        foreach (var rootDir in rootDirs)
        {
            var dir = new DirectoryInfo(rootDir);
            if (!dir.Exists) continue;

            var slnFiles = dir.GetFiles("*.sln")
                .Concat(dir.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length == 1)
            {
                _logger.LogInformation("Found solution via MCP roots: {Path}", slnFiles[0].FullName);
                return slnFiles[0].FullName;
            }
            allSlnFiles.AddRange(slnFiles.Select(f => f.FullName));
        }

        if (allSlnFiles.Count > 1)
        {
            var names = string.Join(", ", allSlnFiles.Select(Path.GetFileName));
            throw new InvalidOperationException(
                $"Multiple solutions found: {names}. Specify which one using the 'solution' parameter (e.g. solution=\"{Path.GetFileName(allSlnFiles[0])}\").");
        }

        if (allSlnFiles.Count == 1)
            return allSlnFiles[0];

        // Fall back to searching from current directory upward
        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (cwd is not null)
        {
            var slnFiles = cwd.GetFiles("*.sln")
                .Concat(cwd.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length == 1)
                return slnFiles[0].FullName;
            if (slnFiles.Length > 1)
            {
                var names = string.Join(", ", slnFiles.Select(f => f.Name));
                throw new InvalidOperationException(
                    $"Multiple solutions found: {names}. Specify which one using the 'solution' parameter.");
            }
            cwd = cwd.Parent;
        }

        throw new InvalidOperationException(
            "No .sln/.slnx file found. Open a workspace containing a solution, or pass --solution <path> in the MCP server args.");
    }

    /// <summary>
    /// Returns local directory paths from MCP roots (workspace folders).
    /// </summary>
    private async Task<List<string>> GetWorkspaceRootDirsAsync(CancellationToken ct)
    {
        var dirs = new List<string>();
        try
        {
            if (_server is not null)
            {
                var roots = await _server.RequestRootsAsync(new ListRootsRequestParams(), ct);
                foreach (var root in roots.Roots)
                {
                    var localPath = FileUriToLocalPath(root.Uri);
                    if (localPath is not null)
                        dirs.Add(localPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP roots discovery failed");
        }
        return dirs;
    }

    /// <summary>
    /// Converts a file:// URI to a local filesystem path.
    /// Handles VS Code's encoding of Windows drive letters (e.g. file:///e%3A/Git/Foo → E:\Git\Foo).
    /// </summary>
    private static string? FileUriToLocalPath(string uriString)
    {
        if (!uriString.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null;

        // Decode percent-encoding first so %3A becomes :
        var decoded = Uri.UnescapeDataString(uriString);

        // Now parse the decoded URI — drive colons are plain text
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;

        return uri.LocalPath;
    }
}
