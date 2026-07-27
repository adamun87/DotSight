using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class GetDocumentSymbolsTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "get_document_symbols", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Get all symbols defined in a source file — types, methods, properties, fields — with their kind, signature, line number, and nesting. Like the document outline in an IDE. Use this to understand the structure of a file before reading specific sections.")]
    public static async Task<string> GetDocumentSymbols(
        WorkspaceService workspace,
        McpServer server,
        [Description("File path relative to the solution directory (e.g., 'Services/WorkspaceService.cs').")] string file,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
        var sln = workspaceSnapshot.Solution;
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";
        var absolutePath = Path.GetFullPath(Path.Combine(solutionDir, file));

        // Find the document
        Document? document = null;
        string? projectName = null;
        foreach (var proj in sln.Projects)
        {
            document = proj.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, absolutePath, StringComparison.OrdinalIgnoreCase));
            if (document is not null)
            {
                projectName = proj.Name;
                break;
            }
        }

        if (document is null)
            return $"File '{file}' not found in the solution.";

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel is null)
            return $"Could not get semantic model for '{file}'.";

        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null)
            return $"Could not parse '{file}'.";

        var sourceText = await document.GetTextAsync(ct);
        var symbols = new List<object>();

        CollectSymbols(root, semanticModel, solutionDir, symbols, depth: 0);

        var result = new
        {
            file,
            project = projectName,
            totalLines = sourceText.Lines.Count,
            symbols
        };

        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    private static void CollectSymbols(
        Microsoft.CodeAnalysis.SyntaxNode node,
        SemanticModel model,
        string solutionDir,
        List<object> symbols,
        int depth)
    {
        foreach (var child in node.ChildNodes())
        {
            var symbol = model.GetDeclaredSymbol(child);
            if (symbol is null || symbol.IsImplicitlyDeclared)
            {
                // Still recurse into namespace/type declarations even if GetDeclaredSymbol returns null
                // (e.g., file-scoped namespaces in some Roslyn versions)
                CollectSymbols(child, model, solutionDir, symbols, depth);
                continue;
            }

            // Skip property accessors, event accessors, lambdas
            if (symbol is IMethodSymbol ms && ms.MethodKind is
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove or
                MethodKind.LambdaMethod or MethodKind.AnonymousFunction)
                continue;

            // Skip local variables, parameters, labels, and range variables — they add noise
            if (symbol is ILocalSymbol or IParameterSymbol or ILabelSymbol or IRangeVariableSymbol)
                continue;

            // Skip the compiler-generated <Main>$ entry point in top-level statement files
            if (symbol is IMethodSymbol entryMethod && entryMethod.Name == "<Main>$")
                continue;

            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            var lineSpan = location?.GetLineSpan();

            var entry = new Dictionary<string, object?>
            {
                ["name"] = symbol.Name,
                ["kind"] = SymbolFormatter.GetKind(symbol),
                ["signature"] = SymbolFormatter.GetSignature(symbol),
                ["line"] = lineSpan is not null ? lineSpan.Value.StartLinePosition.Line + 1 : null,
            };

            if (lineSpan is not null)
            {
                var endLine = lineSpan.Value.EndLinePosition.Line + 1;
                if (endLine != (int)entry["line"]!)
                    entry["endLine"] = endLine;
            }

            if (depth > 0)
                entry["depth"] = depth;

            var summary = SymbolFormatter.GetXmlDocSummary(symbol);
            if (!string.IsNullOrEmpty(summary))
                entry["summary"] = summary;

            // For namespaces and types, collect children
            if (symbol is INamespaceSymbol or INamedTypeSymbol)
            {
                var children = new List<object>();
                CollectSymbols(child, model, solutionDir, children, depth + 1);
                if (children.Count > 0)
                    entry["children"] = children;
            }

            symbols.Add(entry);
        }
    }
}
