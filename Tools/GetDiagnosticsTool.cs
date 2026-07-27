using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Get compiler errors, warnings, and analyzer diagnostics for the solution, a specific project, or a specific file. Returns diagnostics with severity, message, location, and diagnostic ID.")]
    public static async Task<string> GetDiagnostics(
        WorkspaceService workspace,
        McpServer server,
        [Description("Scope: 'solution' to get all diagnostics, or a project name to scope to one project.")] string? scope = null,
        [Description("File path (relative to solution) to filter diagnostics to a specific file.")] string? file = null,
        [Description("Minimum severity: 'error', 'warning', 'info', or 'hidden'. Default: 'warning'.")] string? minSeverity = "warning",
        [Description("Maximum number of diagnostics to return. Default: 100.")] int maxResults = 100,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
        var sln = workspaceSnapshot.Solution;
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";

        var minSev = ParseSeverity(minSeverity);
        var results = new List<object>();

        var projects = string.IsNullOrEmpty(scope) || string.Equals(scope, "solution", StringComparison.OrdinalIgnoreCase)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, scope, StringComparison.OrdinalIgnoreCase));

        string? absoluteFilePath = null;
        if (!string.IsNullOrEmpty(file))
            absoluteFilePath = Path.GetFullPath(Path.Combine(solutionDir, file));

        var errorCount = 0;
        var warningCount = 0;
        var infoCount = 0;

        foreach (var proj in projects)
        {
            if (ct.IsCancellationRequested) break;

            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Get compiler diagnostics
            var diagnostics = compilation.GetDiagnostics(ct);

            // Also get analyzer diagnostics if the project has analyzers loaded
            var analyzerDiags = Enumerable.Empty<Diagnostic>();
            var analyzers = proj.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(proj.Language))
                .ToImmutableArray();
            if (!analyzers.IsEmpty)
            {
                try
                {
                    var analyzerOpts = new CompilationWithAnalyzersOptions(proj.AnalyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false);
                    var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, analyzerOpts);
                    analyzerDiags = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(ct);
                }
                catch
                {
                    // If analyzers fail to load/run, continue with compiler diagnostics only
                }
            }

            foreach (var diag in diagnostics.Concat(analyzerDiags))
            {
                if (diag.Severity < minSev) continue;
                if (diag.IsSuppressed) continue;

                // File filter
                if (absoluteFilePath is not null)
                {
                    var diagPath = diag.Location.GetLineSpan().Path;
                    if (!string.Equals(diagPath, absoluteFilePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Count by severity
                switch (diag.Severity)
                {
                    case DiagnosticSeverity.Error: errorCount++; break;
                    case DiagnosticSeverity.Warning: warningCount++; break;
                    case DiagnosticSeverity.Info: infoCount++; break;
                }

                if (results.Count < maxResults)
                {
                    results.Add(FormatDiagnostic(diag, proj.Name, solutionDir));
                }
            }
        }

        var output = new
        {
            summary = new
            {
                errors = errorCount,
                warnings = warningCount,
                info = infoCount,
                showing = results.Count
            },
            diagnostics = results
        };

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static DiagnosticSeverity ParseSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        "info" => DiagnosticSeverity.Info,
        "hidden" => DiagnosticSeverity.Hidden,
        _ => DiagnosticSeverity.Warning
    };

    private static object FormatDiagnostic(Diagnostic diag, string projectName, string solutionDir)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = diag.Id,
            ["severity"] = diag.Severity.ToString().ToLowerInvariant(),
            ["message"] = diag.GetMessage(),
            ["project"] = projectName,
            ["category"] = diag.Descriptor.Category,
        };

        if (diag.Location.IsInSource)
        {
            var span = diag.Location.GetLineSpan();
            result["file"] = Path.GetRelativePath(solutionDir, span.Path);
            result["line"] = span.StartLinePosition.Line + 1;
            result["column"] = span.StartLinePosition.Character + 1;
        }

        return result;
    }
}
