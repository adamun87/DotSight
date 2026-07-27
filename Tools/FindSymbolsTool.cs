using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class FindSymbolsTool
{
    [McpServerTool(Name = "find_symbols", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Search for symbols (types, methods, properties, etc.) by name pattern across the solution source code and referenced assemblies. Returns matching symbols with their kind, fully qualified name, project, and location.")]
    public static async Task<string> FindSymbols(
        WorkspaceService workspace,
        McpServer server,
        [Description("Name or pattern to search for. Matched as a case-insensitive substring by default. Set 'useRegex' to true for regex matching (e.g., 'Get.*Async' to find all async getters).")] string pattern,
        [Description("Filter by symbol kind: class, interface, struct, enum, delegate, method, property, field, event, namespace. Leave empty for all kinds.")] string? kind = null,
        [Description("Scope the search to a specific project name. Leave empty to search the entire solution.")] string? project = null,
        [Description("If true, interpret 'pattern' as a .NET regular expression instead of a substring. Default: false.")] bool useRegex = false,
        [Description("If true, also search metadata references (NuGet packages, framework assemblies). Default: false.")] bool includeMetadata = false,
        [Description("Maximum number of results to return. Default: 50.")] int maxResults = 50,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
            }
            catch (RegexParseException ex)
            {
                return $"Invalid regex pattern: {ex.Message}";
            }
        }
        using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
        var sln = workspaceSnapshot.Solution;
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";
        var results = new List<object>();
        var seenSymbols = new HashSet<string>();

        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        foreach (var proj in projects)
        {
            if (ct.IsCancellationRequested) break;

            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Search source symbols
            SearchSymbols(compilation.GlobalNamespace, pattern, regex, kind, proj.Name, solutionDir, results, seenSymbols, maxResults, sourceOnly: true);

            // Optionally search metadata references
            if (includeMetadata && results.Count < maxResults)
            {
                foreach (var reference in compilation.References)
                {
                    if (results.Count >= maxResults) break;
                    var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                    if (assemblySymbol is null) continue;

                    SearchSymbols(assemblySymbol.GlobalNamespace, pattern, regex, kind, proj.Name, solutionDir,
                        results, seenSymbols, maxResults, sourceOnly: false, assemblyName: assemblySymbol.Name);
                }
            }

            if (results.Count >= maxResults) break;
        }

        if (results.Count == 0)
            return "No symbols found matching the given criteria.";

        return JsonSerializer.Serialize(new { count = results.Count, symbols = results },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
    }

    private static void SearchSymbols(
        INamespaceSymbol ns,
        string pattern,
        Regex? regex,
        string? kind,
        string projectName,
        string solutionDir,
        List<object> results,
        HashSet<string> seenSymbols,
        int maxResults,
        bool sourceOnly,
        string? assemblyName = null)
    {
        if (results.Count >= maxResults) return;

        foreach (var member in ns.GetMembers())
        {
            if (results.Count >= maxResults) return;

            if (member is INamespaceSymbol childNs)
            {
                // In metadata search, skip deep framework internals that produce noise,
                // but allow useful namespaces like Microsoft.Extensions.*, ModelContextProtocol.*, etc.
                if (!sourceOnly && ShouldSkipMetadataNamespace(childNs))
                    continue;

                SearchSymbols(childNs, pattern, regex, kind, projectName, solutionDir, results, seenSymbols, maxResults,
                    sourceOnly, assemblyName);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (sourceOnly && !type.Locations.Any(l => l.IsInSource))
                    continue;

                if (MatchesPattern(type.Name, pattern, regex) && SymbolFormatter.MatchesKindFilter(type, kind))
                {
                    var key = $"{assemblyName ?? projectName}:"
                        + $"{SymbolFormatter.GetFullyQualifiedName(type)}:"
                        + SymbolFormatter.GetSignature(type);
                    if (seenSymbols.Add(key))
                        results.Add(FormatSymbolResult(type, projectName, solutionDir, assemblyName));
                }

                // Also search members of matching or all types
                if (results.Count < maxResults)
                {
                    foreach (var typeMember in type.GetMembers())
                    {
                        if (results.Count >= maxResults) break;
                        if (typeMember.IsImplicitlyDeclared) continue;
                        if (sourceOnly && !typeMember.Locations.Any(l => l.IsInSource)) continue;

                        if (MatchesPattern(typeMember.Name, pattern, regex) &&
                            SymbolFormatter.MatchesKindFilter(typeMember, kind))
                        {
                            var memberKey = $"{assemblyName ?? projectName}:"
                                + $"{SymbolFormatter.GetFullyQualifiedName(typeMember)}:"
                                + SymbolFormatter.GetSignature(typeMember);
                            if (seenSymbols.Add(memberKey))
                                results.Add(FormatSymbolResult(typeMember, projectName, solutionDir, assemblyName));
                        }
                    }
                }
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern, Regex? regex)
    {
        if (regex is not null)
            return regex.IsMatch(name);
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static object FormatSymbolResult(ISymbol symbol, string projectName, string solutionDir, string? assemblyName)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolFormatter.GetKind(symbol),
            ["fullyQualifiedName"] = SymbolFormatter.GetFullyQualifiedName(symbol),
            ["project"] = projectName,
            ["signature"] = SymbolFormatter.GetSignature(symbol),
        };

        var location = symbol.Locations.FirstOrDefault();
        if (location is not null)
            result["location"] = SymbolFormatter.FormatLocation(location, solutionDir);

        if (assemblyName is not null)
            result["assembly"] = assemblyName;

        var summary = SymbolFormatter.GetXmlDocSummary(symbol);
        if (!string.IsNullOrEmpty(summary))
            result["summary"] = summary;

        return result;
    }

    /// <summary>
    /// Determines whether a metadata namespace should be skipped during search.
    /// Skips deep framework internals (System.Runtime.*, Microsoft.CodeAnalysis.Internal, etc.)
    /// but allows useful namespaces like Microsoft.Extensions.DependencyInjection, ModelContextProtocol, etc.
    /// </summary>
    private static bool ShouldSkipMetadataNamespace(INamespaceSymbol ns)
    {
        // Always skip these — pure runtime/compiler internals
        if (ns.Name is "Internal" or "FxResources" or "Interop")
            return true;

        // Build the full namespace path to make smarter decisions
        var fullName = ns.ToDisplayString();

        // Skip System.* except System.ComponentModel (has Description attribute)
        if (fullName.StartsWith("System.", StringComparison.Ordinal) &&
            !fullName.StartsWith("System.ComponentModel", StringComparison.Ordinal))
            return true;

        // Skip bare "System" namespace internals (but not "System" itself at top level)
        if (fullName == "System")
            return true;

        // Skip Roslyn/MSBuild internals — huge and noisy
        if (fullName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) ||
            fullName.StartsWith("Microsoft.Build", StringComparison.Ordinal) ||
            fullName.StartsWith("Microsoft.Cci", StringComparison.Ordinal) ||
            fullName.StartsWith("Microsoft.DiaSymReader", StringComparison.Ordinal) ||
            fullName.StartsWith("Roslyn.", StringComparison.Ordinal))
            return true;

        // Skip .NET runtime internals
        if (fullName.StartsWith("Microsoft.Win32", StringComparison.Ordinal) ||
            fullName.StartsWith("Microsoft.VisualStudio", StringComparison.Ordinal))
            return true;

        // Allow everything else: Microsoft.Extensions.*, ModelContextProtocol.*, user packages, etc.
        return false;
    }
}
