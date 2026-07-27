using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class GetSymbolDetailTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "get_symbol_detail", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Get detailed information about a specific symbol by its fully qualified name. Returns full signature, documentation, source location/spans, members (for types), base types, interfaces, and extension methods.")]
    public static async Task<string> GetSymbolDetail(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified name of the symbol (e.g., 'MyNamespace.MyClass' or 'MyNamespace.MyClass.MyMethod').")] string fullyQualifiedName,
        [Description("Project name to search in. If omitted, searches all projects.")] string? project = null,
        [Description("Filter members by accessibility: 'public', 'internal', 'protected', 'private'. If omitted, shows all members. Only applies to type details.")] string? memberAccessibility = null,
        [Description("Filter members by kind: 'method', 'property', 'field', 'event', 'constructor'. If omitted, shows all kinds. Only applies to type details.")] string? memberKind = null,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
        var sln = workspaceSnapshot.Solution;
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";

        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        // Try to resolve as a type first, then as a member
        foreach (var proj in projects)
        {
            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Try as a type
            var typeSymbol = WorkspaceService.ResolveType(compilation, fullyQualifiedName);
            if (typeSymbol is not null)
                return FormatTypeDetail(typeSymbol, proj.Name, solutionDir, compilation, memberAccessibility, memberKind);

            // Try as Type.Member — split at the last dot
            var lastDot = fullyQualifiedName.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typePart = fullyQualifiedName[..lastDot];
                var memberPart = fullyQualifiedName[(lastDot + 1)..];
                var parentType = WorkspaceService.ResolveType(compilation, typePart);
                if (parentType is not null)
                {
                    var members = parentType.GetMembers(memberPart);
                    if (members.Length > 0)
                        return FormatMemberDetail(members, parentType, proj.Name, solutionDir);
                }
            }
        }

        return $"Symbol '{fullyQualifiedName}' not found. Check the fully qualified name and project scope.";
    }

    private static string FormatTypeDetail(INamedTypeSymbol type, string projectName, string solutionDir, Compilation compilation, string? memberAccessibility, string? memberKind)
    {
        var detail = new Dictionary<string, object?>
        {
            ["name"] = type.Name,
            ["kind"] = SymbolFormatter.GetKind(type),
            ["fullyQualifiedName"] = SymbolFormatter.GetFullyQualifiedName(type),
            ["project"] = projectName,
            ["signature"] = SymbolFormatter.GetSignature(type),
            ["accessibility"] = type.DeclaredAccessibility.ToString().ToLowerInvariant(),
            ["isAbstract"] = type.IsAbstract,
            ["isSealed"] = type.IsSealed,
            ["isStatic"] = type.IsStatic,
        };

        // Base type
        if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
            detail["baseType"] = SymbolFormatter.GetFullyQualifiedName(type.BaseType);

        // Interfaces
        var interfaces = type.Interfaces;
        if (interfaces.Length > 0)
            detail["interfaces"] = interfaces.Select(SymbolFormatter.GetFullyQualifiedName).ToList();

        // Type parameters
        if (type.TypeParameters.Length > 0)
        {
            detail["typeParameters"] = type.TypeParameters.Select(tp => new
            {
                name = tp.Name,
                constraints = tp.ConstraintTypes.Select(SymbolFormatter.GetFullyQualifiedName).ToList()
            }).ToList();
        }

        // Source locations
        var sourceLocations = type.Locations
            .Where(l => l.IsInSource)
            .Select(l => SymbolFormatter.FormatLocation(l, solutionDir))
            .ToList();
        if (sourceLocations.Count > 0)
            detail["sourceLocations"] = sourceLocations;
        else
            detail["location"] = SymbolFormatter.FormatLocation(type.Locations.FirstOrDefault()!, solutionDir);

        // Documentation
        var summary = SymbolFormatter.GetXmlDocSummary(type);
        if (!string.IsNullOrEmpty(summary))
            detail["documentation"] = summary;

        // Members grouped by kind
        var filteredMembers = type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m is not IMethodSymbol ms || ms.MethodKind is not (
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove or
                MethodKind.EventRaise));

        if (!string.IsNullOrEmpty(memberAccessibility))
        {
            filteredMembers = filteredMembers.Where(m =>
                m.DeclaredAccessibility.ToString().Equals(memberAccessibility, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(memberKind))
        {
            filteredMembers = filteredMembers.Where(m =>
                SymbolFormatter.MatchesKindFilter(m, memberKind));
        }

        var members = filteredMembers
            .GroupBy(m => SymbolFormatter.GetKind(m))
            .ToDictionary(
                g => g.Key + "s",
                g => g.Select(m => FormatMemberSummary(m, solutionDir)).ToList() as object);
        detail["members"] = members;

        // Extension methods (search other types in compilation for static methods with 'this' parameter of this type)
        var extensions = FindExtensionMethods(type, compilation);
        if (extensions.Count > 0)
            detail["extensionMethods"] = extensions;

        return JsonSerializer.Serialize(detail, SerializerOptions);
    }

    private static object FormatMemberSummary(ISymbol member, string solutionDir)
    {
        var info = new Dictionary<string, object?>
        {
            ["name"] = member.Name,
            ["signature"] = SymbolFormatter.GetSignature(member),
            ["accessibility"] = member.DeclaredAccessibility.ToString().ToLowerInvariant(),
        };

        // Add type info for methods and properties
        if (member is IMethodSymbol method)
            info["returnType"] = method.ReturnType.ToDisplayString();
        else if (member is IPropertySymbol prop)
            info["type"] = prop.Type.ToDisplayString();
        else if (member is IFieldSymbol field)
            info["type"] = field.Type.ToDisplayString();

        // Add source location
        var location = member.Locations.FirstOrDefault(l => l.IsInSource) ?? member.Locations.FirstOrDefault();
        if (location is not null)
            info["location"] = SymbolFormatter.FormatLocation(location, solutionDir);

        return info;
    }

    private static string FormatMemberDetail(
        System.Collections.Immutable.ImmutableArray<ISymbol> members,
        INamedTypeSymbol containingType,
        string projectName,
        string solutionDir)
    {
        var results = members.Select(m =>
        {
            var info = new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["kind"] = SymbolFormatter.GetKind(m),
                ["containingType"] = SymbolFormatter.GetFullyQualifiedName(containingType),
                ["project"] = projectName,
                ["signature"] = SymbolFormatter.GetSignature(m),
                ["accessibility"] = m.DeclaredAccessibility.ToString().ToLowerInvariant(),
            };

            if (m is IMethodSymbol method)
            {
                info["returnType"] = method.ReturnType.ToDisplayString();
                info["isAsync"] = method.IsAsync;
                info["isStatic"] = method.IsStatic;
                info["parameters"] = method.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
                }).ToList();

                if (method.OverriddenMethod is not null)
                    info["overrides"] = SymbolFormatter.GetFullyQualifiedName(method.OverriddenMethod);
            }
            else if (m is IPropertySymbol prop)
            {
                info["type"] = prop.Type.ToDisplayString();
                info["hasGetter"] = prop.GetMethod is not null;
                info["hasSetter"] = prop.SetMethod is not null;
                info["isIndexer"] = prop.IsIndexer;
            }
            else if (m is IFieldSymbol field)
            {
                info["type"] = field.Type.ToDisplayString();
                info["isConst"] = field.IsConst;
                info["isReadOnly"] = field.IsReadOnly;
                if (field.HasConstantValue)
                    info["constantValue"] = field.ConstantValue?.ToString();
            }

            var location = m.Locations.FirstOrDefault();
            if (location is not null)
                info["location"] = SymbolFormatter.FormatLocation(location, solutionDir);

            var summary = SymbolFormatter.GetXmlDocSummary(m);
            if (!string.IsNullOrEmpty(summary))
                info["documentation"] = summary;

            return info;
        }).ToList();

        object output = results.Count == 1 ? results[0] : new { overloads = results };
        return JsonSerializer.Serialize(output, SerializerOptions);
    }

    private static List<object> FindExtensionMethods(INamedTypeSymbol targetType, Compilation compilation)
    {
        var results = new List<object>();
        var maxExtensions = 20;

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                if (results.Count >= maxExtensions) return results;

                var symbol = semanticModel.GetDeclaredSymbol(node);
                if (symbol is IMethodSymbol method &&
                    method.IsExtensionMethod &&
                    method.Parameters.Length > 0)
                {
                    var firstParamType = method.Parameters[0].Type;
                    if (SymbolEqualityComparer.Default.Equals(firstParamType, targetType) ||
                        (firstParamType is INamedTypeSymbol namedParam &&
                         targetType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, namedParam))))
                    {
                        results.Add(new
                        {
                            name = method.Name,
                            signature = SymbolFormatter.GetSignature(method),
                            containingType = SymbolFormatter.GetFullyQualifiedName(method.ContainingType)
                        });
                    }
                }
            }
        }

        return results;
    }
}
