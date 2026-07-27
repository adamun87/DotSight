using System.ComponentModel;
using System.Text.Json;
using DotSight.Services;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class PreviewRenameTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "preview_rename", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Use Roslyn rename semantics to preview every source edit required to rename one exact C# symbol. Returns file checksums, ranges, old/new text, file rename metadata, and newly introduced compiler errors. Never modifies files. Select by fully qualified name plus optional signature, or by file/line/column when overloads are ambiguous.")]
    public static async Task<string> PreviewRename(
        WorkspaceService workspace,
        McpServer server,
        [Description("New C# identifier.")] string newName,
        [Description("Fully qualified symbol name. May be omitted when file, line, and column are provided.")] string? fullyQualifiedName = null,
        [Description("Exact or trailing symbol signature used to disambiguate overloads.")] string? signature = null,
        [Description("Project name to scope symbol resolution. Recommended for multi-target or duplicate project symbols.")] string? project = null,
        [Description("Source file path relative to the solution, used together with line and column for exact position selection.")] string? file = null,
        [Description("One-based source line for exact position selection.")] int? line = null,
        [Description("One-based source column for exact position selection.")] int? column = null,
        [Description("Rename all method overloads. Default: false.")] bool renameOverloads = false,
        [Description("Rename matching identifiers inside string literals. Default: false.")] bool renameInStrings = false,
        [Description("Rename matching identifiers inside comments. Default: false.")] bool renameInComments = false,
        [Description("Preview renaming a matching type file as well. Default: false.")] bool renameFile = false,
        [Description("Maximum individual text edits to return, from 1 to 5000. Default: 1000.")] int maxEdits = 1000,
        [Description("Compile changed projects and report newly introduced errors. Default: true.")] bool includeDiagnostics = true,
        [Description("Solution or project file to load. If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        if (string.IsNullOrWhiteSpace(newName) || !SyntaxFacts.IsValidIdentifier(newName))
        {
            return JsonSerializer.Serialize(
                new { error = $"'{newName}' is not a valid C# identifier." },
                SerializerOptions);
        }

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

        if (!resolution.Match!.Symbol.Locations.Any(location => location.IsInSource))
        {
            return JsonSerializer.Serialize(
                new
                {
                    workspace = workspace.GetSnapshotInfo(),
                    error = "Metadata symbols cannot be renamed because they have no source declaration in this solution.",
                },
                SerializerOptions);
        }

        if (string.Equals(resolution.Match.Symbol.Name, newName, StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(
                new
                {
                    workspace = workspace.GetSnapshotInfo(),
                    error = $"The symbol is already named '{newName}'.",
                },
                SerializerOptions);
        }

        var preview = await RenamePreviewService.PreviewAsync(
            snapshot,
            resolution.Match,
            newName,
            new RenamePreviewOptions(
                renameOverloads,
                renameInStrings,
                renameInComments,
                renameFile),
            maxEdits,
            includeDiagnostics,
            ct);
        return JsonSerializer.Serialize(
            new
            {
                workspace = workspace.GetSnapshotInfo(),
                preview,
            },
            SerializerOptions);
    }
}
