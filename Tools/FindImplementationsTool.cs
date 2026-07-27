using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class FindImplementationsTool
{
    [McpServerTool(Name = "find_implementations", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Find concrete implementations, derived classes, or overrides for one exact symbol. Returns truthful totals and truncation. Select by fully qualified name plus optional signature, or by file/line/column when overloads are ambiguous.")]
    public static async Task<string> FindImplementations(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified symbol name. May be omitted when file, line, and column are provided.")] string? fullyQualifiedName = null,
        [Description("Project name where the symbol is defined. If omitted, searches all projects.")] string? project = null,
        [Description("Exact or trailing signature used to disambiguate overloads.")] string? signature = null,
        [Description("Source file path relative to the solution, used together with line and column for exact position selection.")] string? file = null,
        [Description("One-based source line for exact position selection.")] int? line = null,
        [Description("One-based source column for exact position selection.")] int? column = null,
        [Description("Maximum number of implementations to return. Default: 50.")] int maxResults = 50,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
        var sln = workspaceSnapshot.Solution;
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";
        maxResults = Math.Clamp(maxResults, 1, 1000);
        var resolution = await SymbolResolver.ResolveAsync(
            sln,
            new SymbolSelector(fullyQualifiedName, signature, project, file, line, column),
            ct);
        if (!resolution.Succeeded)
            return JsonSerializer.Serialize(resolution.ToErrorPayload(), SerializerOptions);

        var target = resolution.Match!;
        var implementationSymbols = new List<ISymbol>();

        if (target.Symbol is INamedTypeSymbol typeSymbol)
        {
            // Find implementations of a type
            IEnumerable<INamedTypeSymbol> impls;

            if (typeSymbol.TypeKind == TypeKind.Interface)
                impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, sln, cancellationToken: ct);
            else
                impls = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, sln, cancellationToken: ct);

            implementationSymbols.AddRange(impls);
        }
        else if (target.Symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // Find overrides of a member
            var overrides = await SymbolFinder.FindOverridesAsync(target.Symbol, sln, cancellationToken: ct);
            implementationSymbols.AddRange(overrides);

            // Also find interface implementations
            if (target.Symbol.ContainingType?.TypeKind == TypeKind.Interface)
            {
                var memberImpls = await SymbolFinder.FindImplementationsAsync(
                    target.Symbol,
                    sln,
                    cancellationToken: ct);
                implementationSymbols.AddRange(memberImpls);
            }
        }

        var implementations = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalImplementations = 0;
        foreach (var symbol in implementationSymbols)
        {
            var symbolProject = await SymbolResolver.FindProjectForSymbolAsync(
                    sln,
                    symbol,
                    target.Project,
                    ct)
                ?? target.Project;
            if (!seen.Add(SymbolResolver.GetIdentity(symbol, symbolProject)))
                continue;

            totalImplementations++;
            if (implementations.Count < maxResults)
            {
                implementations.Add(
                    FormatImplementation(symbol, symbolProject, solutionDir));
            }
        }

        var result = new
        {
            symbol = new
            {
                name = target.Symbol.Name,
                kind = SymbolFormatter.GetKind(target.Symbol),
                fullyQualifiedName = SymbolFormatter.GetFullyQualifiedName(target.Symbol),
                signature = SymbolFormatter.GetSignature(target.Symbol),
                project = target.Project.Name
            },
            totalImplementations,
            returnedImplementations = implementations.Count,
            truncated = totalImplementations > implementations.Count,
            implementations
        };

        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    private static object FormatImplementation(
        ISymbol symbol,
        Project project,
        string solutionDir)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? symbol.Locations.FirstOrDefault();

        var info = new Dictionary<string, object?>
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolFormatter.GetKind(symbol),
            ["fullyQualifiedName"] = SymbolFormatter.GetFullyQualifiedName(symbol),
            ["signature"] = SymbolFormatter.GetSignature(symbol),
            ["isAbstract"] = symbol is INamedTypeSymbol { IsAbstract: true },
            ["project"] = project.Name,
        };

        if (location is not null)
            info["location"] = SymbolFormatter.FormatLocation(location, solutionDir);

        return info;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
