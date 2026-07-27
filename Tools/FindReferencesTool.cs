using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class FindReferencesTool
{
    [McpServerTool(Name = "find_references", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Find references to one exact symbol across the solution. Returns declarations and reference locations, with explicit truncation. Select by fully qualified name plus optional signature, or by file/line/column when overloads are ambiguous.")]
    public static async Task<string> FindReferences(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified symbol name. May be omitted when file, line, and column are provided.")] string? fullyQualifiedName = null,
        [Description("Project name where the symbol is defined. If omitted, searches all projects.")] string? project = null,
        [Description("Exact or trailing signature used to disambiguate overloads.")] string? signature = null,
        [Description("Source file path relative to the solution, used together with line and column for exact position selection.")] string? file = null,
        [Description("One-based source line for exact position selection.")] int? line = null,
        [Description("One-based source column for exact position selection.")] int? column = null,
        [Description("Maximum number of reference locations to return. Default: 100.")] int maxResults = 100,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        var sln = await workspace.GetSolutionAsync(solution, ct);
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";
        maxResults = Math.Clamp(maxResults, 1, 1000);
        var resolution = await SymbolResolver.ResolveAsync(
            sln,
            new SymbolSelector(fullyQualifiedName, signature, project, file, line, column),
            ct);
        if (!resolution.Succeeded)
            return JsonSerializer.Serialize(resolution.ToErrorPayload(), SerializerOptions);

        // Find all references
        var target = resolution.Match!;
        var references = await SymbolFinder.FindReferencesAsync(target.Symbol, sln, ct);
        var locations = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalLocations = 0;

        foreach (var refGroup in references)
        {
            var definitionProject = await SymbolResolver.FindProjectForSymbolAsync(
                sln,
                refGroup.Definition,
                target.Project,
                ct);

            // Add the definition locations
            foreach (var defLocation in refGroup.Definition.Locations)
            {
                if (!defLocation.IsInSource) continue;
                AddLocation(
                    defLocation,
                    "declaration",
                    definitionProject?.Id,
                    definitionProject?.Name);
            }

            // Add reference locations
            foreach (var refLocation in refGroup.Locations)
            {
                var loc = refLocation.Location;
                if (!loc.IsInSource) continue;

                var classification = ClassifyReference(refLocation);
                AddLocation(
                    loc,
                    classification,
                    refLocation.Document.Project.Id,
                    refLocation.Document.Project.Name);
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
            totalLocations,
            returnedLocations = locations.Count,
            truncated = totalLocations > locations.Count,
            references = locations
        };

        return JsonSerializer.Serialize(result, SerializerOptions);

        void AddLocation(
            Location location,
            string classification,
            ProjectId? projectId,
            string? projectName)
        {
            var identity = $"{projectId?.Id:N}:{location.GetLineSpan().Path}:"
                + $"{location.SourceSpan.Start}:{location.SourceSpan.Length}:{classification}";
            if (!seen.Add(identity))
                return;

            totalLocations++;
            if (locations.Count < maxResults)
            {
                locations.Add(
                    FormatReferenceLocation(
                        location,
                        classification,
                        projectName,
                        solutionDir));
            }
        }
    }

    private static object FormatReferenceLocation(
        Location location,
        string classification,
        string? project,
        string solutionDir)
    {
        var span = location.GetLineSpan();
        var filePath = Path.GetRelativePath(solutionDir, span.Path);

        return new
        {
            file = filePath,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
            endLine = span.EndLinePosition.Line + 1,
            endColumn = span.EndLinePosition.Character + 1,
            classification,
            project
        };
    }

    private static string ClassifyReference(ReferenceLocation refLocation)
    {
        if (refLocation.IsImplicit)
            return "implicit";
        return "reference";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
