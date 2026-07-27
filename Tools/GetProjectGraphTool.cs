using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class GetProjectGraphTool
{
    [McpServerTool(Name = "get_project_graph", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Get a broad project and dependency overview. Returns target frameworks, output types, package references, optional source files, and optional code outlines. Use it when the task needs an architecture map; targeted symbol tools do not require this call first.")]
    public static async Task<string> GetProjectGraph(
        WorkspaceService workspace,
        McpServer server,
        [Description("Specific project name to get details for. If omitted, returns the entire solution graph.")] string? project = null,
        [Description("If true, include the full list of package references for each project. Default: true.")] bool includePackages = true,
        [Description("If true, include the list of source file paths for each project. Useful for understanding project layout. Default: false.")] bool includeFiles = false,
        [Description("If true, include a compact code outline per file showing types, methods, and properties. Gives a full architecture overview in a single call. Default: false.")] bool includeOutlines = false,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). Supports .sln, .slnx, and .csproj. If omitted, auto-detected. Required when the workspace contains multiple solutions.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        var sln = await workspace.GetSolutionAsync(solution, ct);
        var solutionDir = Path.GetDirectoryName(sln.FilePath ?? sln.Projects.FirstOrDefault()?.FilePath) ?? "";

        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects.ToList()
            : sln.Projects
                .Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var projectNodes = new List<object>();

        foreach (var proj in projects)
        {
            if (ct.IsCancellationRequested) break;

            var node = new Dictionary<string, object?>
            {
                ["name"] = proj.Name,
                ["assemblyName"] = proj.AssemblyName,
                ["language"] = proj.Language,
                ["filePath"] = Path.GetRelativePath(solutionDir, proj.FilePath ?? ""),
            };

            // Output kind
            node["outputKind"] = proj.CompilationOptions?.OutputKind switch
            {
                OutputKind.ConsoleApplication => "exe",
                OutputKind.WindowsApplication => "winexe",
                OutputKind.DynamicallyLinkedLibrary => "library",
                OutputKind.NetModule => "module",
                OutputKind.WindowsRuntimeMetadata => "winmd",
                OutputKind.WindowsRuntimeApplication => "appcontainer",
                _ => "unknown"
            };

            // Parse properties from the project file for TFM
            node["targetFramework"] = ExtractTargetFramework(proj);

            // Project references (dependency edges)
            var projectRefs = proj.ProjectReferences
                .Select(pr =>
                {
                    var refProject = sln.GetProject(pr.ProjectId);
                    return refProject?.Name;
                })
                .Where(n => n is not null)
                .ToList();
            node["projectReferences"] = projectRefs;

            // Package references — parse from project file for clean direct references
            if (includePackages)
            {
                var packages = await ExtractPackageReferencesAsync(proj.FilePath, ct);
                node["packageReferences"] = packages;
            }

            // Source file count
            node["documentCount"] = proj.Documents.Count();
            node["additionalDocumentCount"] = proj.AdditionalDocuments.Count();

            // Source file list
            if (includeFiles || includeOutlines)
            {
                var projDir = Path.GetDirectoryName(proj.FilePath) ?? solutionDir;
                var userDocs = proj.Documents
                    .Where(d => d.FilePath is not null
                        && !d.FilePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !d.FilePath.Contains($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}")
                        && !Path.IsPathRooted(Path.GetRelativePath(projDir, d.FilePath)) // skip files outside project dir
                    )
                    .Where(d => !Path.GetRelativePath(projDir, d.FilePath!).StartsWith(".."))
                    .OrderBy(d => d.FilePath)
                    .ToList();

                if (includeOutlines)
                {
                    // Compact code outline per file: types and their members
                    var fileOutlines = new List<object>();
                    foreach (var doc in userDocs)
                    {
                        var relativePath = Path.GetRelativePath(projDir, doc.FilePath!).Replace('\\', '/');
                        var outline = await ExtractFileOutlineAsync(doc, ct);
                        if (outline.Count > 0)
                            fileOutlines.Add(new { file = relativePath, symbols = outline });
                        else
                            fileOutlines.Add(new { file = relativePath, symbols = (object?)null });
                    }
                    node["fileOutlines"] = fileOutlines;
                }
                else
                {
                    // Just file names
                    node["files"] = userDocs
                        .Select(d => Path.GetRelativePath(projDir, d.FilePath!).Replace('\\', '/'))
                        .ToList();
                }
            }

            projectNodes.Add(node);
        }

        // Build the dependency graph edges
        var edges = new List<object>();
        foreach (var proj in projects)
        {
            foreach (var projRef in proj.ProjectReferences)
            {
                var targetProject = sln.GetProject(projRef.ProjectId);
                if (targetProject is not null)
                {
                    edges.Add(new { from = proj.Name, to = targetProject.Name });
                }
            }
        }

        var result = new
        {
            solutionPath = Path.GetFileName(sln.FilePath ?? sln.Projects.FirstOrDefault()?.FilePath),
            totalProjects = sln.Projects.Count(),
            showingProjects = projectNodes.Count,
            projects = projectNodes,
            dependencyEdges = edges
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Extracts a compact outline from a document: top-level types and their public/internal members.
    /// </summary>
    private static async Task<List<object>> ExtractFileOutlineAsync(Document doc, CancellationToken ct)
    {
        var outline = new List<object>();
        var tree = await doc.GetSyntaxTreeAsync(ct);
        var model = await doc.GetSemanticModelAsync(ct);
        if (tree is null || model is null) return outline;

        var root = await tree.GetRootAsync(ct);

        // Walk top-level type declarations (classes, interfaces, structs, enums, records)
        foreach (var typeNode in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var typeSymbol = model.GetDeclaredSymbol(typeNode) as INamedTypeSymbol;
            if (typeSymbol is null || typeSymbol.IsImplicitlyDeclared) continue;

            // Skip nested types — they'll appear as members of their parent
            if (typeSymbol.ContainingType is not null) continue;

            var entry = new Dictionary<string, object?>
            {
                ["name"] = typeSymbol.Name,
                ["kind"] = SymbolFormatter.GetKind(typeSymbol),
            };

            // Base types and interfaces (compact)
            if (typeSymbol.BaseType is not null && typeSymbol.BaseType.SpecialType == SpecialType.None
                && typeSymbol.BaseType.Name != "Object" && typeSymbol.BaseType.Name != "ValueType")
                entry["baseType"] = typeSymbol.BaseType.Name;

            var ifaces = typeSymbol.Interfaces.Select(i => i.Name).ToList();
            if (ifaces.Count > 0)
                entry["interfaces"] = ifaces;

            // XML doc summary
            var summary = SymbolFormatter.GetXmlDocSummary(typeSymbol);
            if (!string.IsNullOrEmpty(summary))
                entry["summary"] = summary;

            // Collect member names with kinds (compact: just name + kind)
            var members = new List<string>();
            foreach (var m in typeSymbol.GetMembers())
            {
                if (m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility is Accessibility.Private) continue;
                if (m is IMethodSymbol ms && ms.MethodKind is
                    MethodKind.PropertyGet or MethodKind.PropertySet or
                    MethodKind.EventAdd or MethodKind.EventRemove)
                    continue;

                // Compact format: "MethodName()" for methods, "PropertyName" for properties
                var memberStr = m switch
                {
                    IMethodSymbol method => $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.Name))})",
                    IPropertySymbol prop => prop.Name,
                    IFieldSymbol field => field.Name,
                    IEventSymbol evt => evt.Name,
                    INamedTypeSymbol nested => $"{nested.TypeKind.ToString().ToLowerInvariant()} {nested.Name}",
                    _ => m.Name
                };
                members.Add(memberStr);
            }
            if (members.Count > 0)
                entry["members"] = members;

            outline.Add(entry);
        }

        return outline;
    }

    private static string ExtractTargetFramework(Project project)
    {
        // Try to read TFM from the project's parse options or compilation options
        // The most reliable way is from the project file path convention or compilation defines
        if (project.ParseOptions is Microsoft.CodeAnalysis.CSharp.CSharpParseOptions csOptions)
        {
            var defines = csOptions.PreprocessorSymbolNames;
            // Look for TFM-related defines like NET8_0, NET9_0, etc.
            var tfmDefine = defines.FirstOrDefault(d =>
                d.StartsWith("NET") && d.Contains('_'));
            if (tfmDefine is not null)
            {
                // Convert NET8_0 -> net8.0, NET8_0_OR_GREATER -> net8.0
                var parts = tfmDefine.Split('_');
                if (parts.Length >= 2)
                {
                    var prefix = parts[0].ToLowerInvariant(); // "net8" or "netcoreapp3"
                    var minor = parts[1];
                    if (minor != "OR")
                        return $"{prefix}.{minor}";
                }
            }
        }

        return "unknown";
    }

    private static async Task<List<object>> ExtractPackageReferencesAsync(string? projectFilePath, CancellationToken ct)
    {
        var packages = new List<object>();
        if (string.IsNullOrEmpty(projectFilePath) || !File.Exists(projectFilePath))
            return packages;

        var xml = await File.ReadAllTextAsync(projectFilePath, ct);
        var doc = System.Xml.Linq.XDocument.Parse(xml);

        foreach (var element in doc.Descendants("PackageReference"))
        {
            var name = element.Attribute("Include")?.Value;
            var version = element.Attribute("Version")?.Value ?? element.Element("Version")?.Value;
            if (name is not null)
            {
                packages.Add(new { name, version = version ?? "*" });
            }
        }

        return packages;
    }
}
