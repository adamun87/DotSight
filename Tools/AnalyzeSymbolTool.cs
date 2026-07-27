using System.ComponentModel;
using System.Text.Json;
using DotSight.Services;
using ModelContextProtocol.Server;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class AnalyzeSymbolTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "analyze_symbol", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Analyze the impact and static call flow of one exact C# symbol. Returns actionable references, incoming callers, outgoing invocations/constructions, implementations, overrides, enclosing symbols, snippets, and explicit test-project evidence. Select by fully qualified name plus optional signature, or by file/line/column when overloads are ambiguous.")]
    public static async Task<string> AnalyzeSymbol(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified symbol name. May be omitted when file, line, and column are provided.")] string? fullyQualifiedName = null,
        [Description("Exact or trailing symbol signature used to disambiguate overloads.")] string? signature = null,
        [Description("Project name to scope symbol resolution. Recommended for multi-target or duplicate project symbols.")] string? project = null,
        [Description("Source file path relative to the solution, used together with line and column for exact position selection.")] string? file = null,
        [Description("One-based source line for exact position selection.")] int? line = null,
        [Description("One-based source column for exact position selection.")] int? column = null,
        [Description("Incoming/outgoing call traversal depth from 1 to 3. Default: 1.")] int depth = 1,
        [Description("Maximum items returned per section, from 1 to 500. Default: 100.")] int maxResultsPerSection = 100,
        [Description("Solution or project file to load. If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        var snapshot = await workspace.GetSolutionAsync(solution, ct);
        var resolution = await SymbolResolver.ResolveAsync(
            snapshot,
            new SymbolSelector(fullyQualifiedName, signature, project, file, line, column),
            ct);
        if (!resolution.Succeeded)
        {
            return JsonSerializer.Serialize(
                new
                {
                    workspace = workspace.GetSnapshotInfo(),
                    resolution = resolution.ToErrorPayload(),
                },
                SerializerOptions);
        }

        var analysis = await SymbolImpactAnalyzer.AnalyzeAsync(
            snapshot,
            resolution.Match!,
            depth,
            maxResultsPerSection,
            ct);
        return JsonSerializer.Serialize(
            new
            {
                workspace = workspace.GetSnapshotInfo(),
                analysis,
            },
            SerializerOptions);
    }
}
