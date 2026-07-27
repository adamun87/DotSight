using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class InspectPackageTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "inspect_package", ReadOnly = true, Destructive = false, OpenWorld = true),
     Description("Explore a NuGet package's public API without modifying the current project. " +
                 "Works for both new packages and packages already installed in your solution. " +
                 "Returns public types, members, and XML doc summaries with optional filters for precise lookup.")]
    public static async Task<string> InspectPackage(
        WorkspaceService workspace,
        McpServer server,
        [Description("NuGet package name (e.g., 'MediatR', 'Polly', 'FluentValidation').")] string packageName,
        [Description("Optional package version. If omitted, uses the version installed in your current solution when available; otherwise uses the latest stable version.")] string? version = null,
        [Description("Filter types to a specific namespace prefix (e.g., 'MediatR' to skip internal namespaces).")] string? namespaceFilter = null,
        [Description("Filter by type kind: 'interface', 'class', 'struct', 'enum', 'delegate'. If omitted, returns all kinds.")] string? kindFilter = null,
        [Description("Optional free-text query (case-insensitive). Matches type name/signature/summary and member name/signature/summary.")] string? query = null,
        [Description("Optional filter for type name/full name/signature (case-insensitive substring).")]
        string? typeFilter = null,
        [Description("Optional filter for member name/signature/summary (case-insensitive substring).")]
        string? memberFilter = null,
        [Description("Optional member kind filter: 'method', 'property', 'field', 'event', 'constructor'.")]
        string? memberKindFilter = null,
        [Description("Include public members for each type. Default: true.")] bool includeMembers = true,
        [Description("Maximum number of members to return per type. Default: 200.")] int maxMembersPerType = 200,
        [Description("Maximum number of types to return. Default: 200.")] int maxTypes = 200,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        maxTypes = Math.Max(1, maxTypes);
        maxMembersPerType = Math.Max(0, maxMembersPerType);

        var requestedVersion = version;
        if (string.IsNullOrWhiteSpace(version))
            version = await DetectInstalledVersionFromWorkspaceAsync(workspace, solution, packageName, ct);

        // Determine TFM from the current solution's first project
        var tfm = "net8.0"; // safe default
        try
        {
            using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
            var sln = workspaceSnapshot.Solution;
            var firstProject = sln.Projects.FirstOrDefault();
            if (firstProject is not null)
            {
                var parseOptions = firstProject.ParseOptions as CSharpParseOptions;
                var langVersion = parseOptions?.LanguageVersion ?? LanguageVersion.Default;
                // Infer TFM from language version
                tfm = langVersion switch
                {
                    >= LanguageVersion.CSharp13 => "net10.0",
                    >= LanguageVersion.CSharp12 => "net9.0",
                    >= LanguageVersion.CSharp11 => "net8.0",
                    _ => "net8.0"
                };
            }
        }
        catch { /* use default TFM */ }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dotsight_inspect_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create a minimal project that references the package
            var versionAttr = $" Version=\"{EscapeXml(version ?? "*")}\"";
            var csproj = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{tfm}</TargetFramework>
                    <OutputType>Library</OutputType>
                    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{EscapeXml(packageName)}"{versionAttr} />
                  </ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Inspect.csproj"), csproj, ct);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Empty.cs"), "// placeholder", ct);

            // Restore and build to get the DLLs
            var (exitCode, output) = await RunDotnetAsync("build --nologo -v q", tempDir, ct);
            if (exitCode != 0)
                return JsonSerializer.Serialize(new { error = "Failed to build temp project", details = TruncateOutput(output, 2000) }, SerializerOptions);

            // Find the output DLLs
            var outputDir = Path.Combine(tempDir, "bin", "Debug", tfm);
            if (!Directory.Exists(outputDir))
                return JsonSerializer.Serialize(new { error = $"Output directory not found: {outputDir}" }, SerializerOptions);

            // Load all assemblies as metadata references and create a compilation
            var metadataRefs = new List<MetadataReference>();
            foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(dll)); }
                catch { /* skip unloadable assemblies */ }
            }

            // Also add runtime assemblies for framework types
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            foreach (var runtimeDll in new[] { "System.Runtime.dll", "netstandard.dll" })
            {
                var path = Path.Combine(runtimeDir, runtimeDll);
                if (File.Exists(path))
                    metadataRefs.Add(MetadataReference.CreateFromFile(path));
            }

            var compilation = CSharpCompilation.Create("InspectAssembly",
                references: metadataRefs,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Find the package's assemblies (not framework/runtime ones)
            var packageAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                // Skip the placeholder assembly and common runtime assemblies
                if (name is "Inspect" or "System.Runtime" or "netstandard") continue;
                packageAssemblies.Add(name);
            }

            // Extract public API from package assemblies
            var types = new List<object>();
            var installedVersion = DetectInstalledVersion(tempDir, packageName);

            foreach (var asm in compilation.References)
            {
                var asmSymbol = compilation.GetAssemblyOrModuleSymbol(asm) as IAssemblySymbol;
                if (asmSymbol is null) continue;

                var asmName = asmSymbol.Name;
                if (!packageAssemblies.Contains(asmName)) continue;

                CollectPublicTypes(
                    asmSymbol.GlobalNamespace,
                    namespaceFilter,
                    kindFilter,
                    typeFilter,
                    query,
                    memberFilter,
                    memberKindFilter,
                    includeMembers,
                    maxMembersPerType,
                    types,
                    maxTypes);
            }

            var result = new Dictionary<string, object?>
            {
                ["package"] = packageName,
                ["version"] = installedVersion ?? version ?? "latest",
                ["resolvedFromInstalledPackage"] = string.IsNullOrWhiteSpace(requestedVersion) && !string.IsNullOrWhiteSpace(version),
                ["typeCount"] = types.Count,
                ["types"] = types,
            };

            var appliedFilters = BuildAppliedFilters(
                namespaceFilter,
                kindFilter,
                query,
                typeFilter,
                memberFilter,
                memberKindFilter,
                includeMembers,
                maxMembersPerType,
                maxTypes);
            if (appliedFilters.Count > 0)
                result["appliedFilters"] = appliedFilters;

            if (types.Count == 0 && appliedFilters.Count > 0)
            {
                result["hint"] = "No matches found. Relax query/filter values or remove member-only filters first.";

                var allNamespaces = new HashSet<string>();
                foreach (var asm in compilation.References)
                {
                    var asmSymbol = compilation.GetAssemblyOrModuleSymbol(asm) as IAssemblySymbol;
                    if (asmSymbol is null) continue;
                    if (!packageAssemblies.Contains(asmSymbol.Name)) continue;
                    CollectNamespaces(asmSymbol.GlobalNamespace, namespaceFilter: null, allNamespaces);
                }
                result["availableNamespaces"] = allNamespaces.OrderBy(n => n).ToList();
            }

            if (types.Count >= maxTypes)
            {
                result["truncated"] = true;
                result["hint"] = "Results truncated. Narrow with namespaceFilter, typeFilter, query, memberFilter, or memberKindFilter, or increase maxTypes.";

                // Collect all namespaces so the agent knows what to filter on
                var allNamespaces = new HashSet<string>();
                foreach (var asm in compilation.References)
                {
                    var asmSymbol = compilation.GetAssemblyOrModuleSymbol(asm) as IAssemblySymbol;
                    if (asmSymbol is null) continue;
                    if (!packageAssemblies.Contains(asmSymbol.Name)) continue;
                    CollectNamespaces(asmSymbol.GlobalNamespace, namespaceFilter, allNamespaces);
                }
                result["availableNamespaces"] = allNamespaces.OrderBy(n => n).ToList();
            }

            return JsonSerializer.Serialize(result, SerializerOptions);
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CollectPublicTypes(
        INamespaceSymbol ns,
        string? namespaceFilter,
        string? kindFilter,
        string? typeFilter,
        string? query,
        string? memberFilter,
        string? memberKindFilter,
        bool includeMembers,
        int maxMembersPerType,
        List<object> types,
        int maxTypes)
    {
        if (types.Count >= maxTypes) return;

        foreach (var member in ns.GetMembers())
        {
            if (types.Count >= maxTypes) return;

            if (member is INamespaceSymbol childNs)
            {
                // Skip compiler-generated and internal namespaces
                if (childNs.Name.StartsWith("<") || childNs.Name is "Internal" or "Internals")
                    continue;

                CollectPublicTypes(
                    childNs,
                    namespaceFilter,
                    kindFilter,
                    typeFilter,
                    query,
                    memberFilter,
                    memberKindFilter,
                    includeMembers,
                    maxMembersPerType,
                    types,
                    maxTypes);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.DeclaredAccessibility != Accessibility.Public)
                    continue;

                // Apply namespace filter
                var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";
                if (namespaceFilter is not null &&
                    !typeNs.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Apply type text filter
                var fullTypeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                if (!MatchesAny(typeFilter, type.Name, fullTypeName, typeNs))
                    continue;

                // Apply kind filter
                if (kindFilter is not null)
                {
                    var kind = type.TypeKind.ToString().ToLowerInvariant();
                    if (!string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var formatted = FormatType(
                    type,
                    query,
                    memberFilter,
                    memberKindFilter,
                    includeMembers,
                    maxMembersPerType);
                if (formatted is not null)
                    types.Add(formatted);
            }
        }
    }

    private static void CollectNamespaces(
        INamespaceSymbol ns,
        string? namespaceFilter,
        HashSet<string> namespaces)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                if (childNs.Name.StartsWith("<") || childNs.Name is "Internal" or "Internals")
                    continue;

                var nsName = childNs.ToDisplayString();
                if (namespaceFilter is not null &&
                    !nsName.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only add namespaces that contain public types
                if (childNs.GetTypeMembers().Any(t => t.DeclaredAccessibility == Accessibility.Public))
                    namespaces.Add(nsName);

                CollectNamespaces(childNs, namespaceFilter, namespaces);
            }
        }
    }

    private static object? FormatType(
        INamedTypeSymbol type,
        string? query,
        string? memberFilter,
        string? memberKindFilter,
        bool includeMembers,
        int maxMembersPerType)
    {
        var namespaceName = type.ContainingNamespace?.ToDisplayString();
        var typeSignature = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var typeSummary = SymbolFormatter.GetXmlDocSummary(type);
        var typeMatchesQuery = MatchesAny(query, type.Name, namespaceName, typeSignature, typeSummary);

        var result = new Dictionary<string, object?>
        {
            ["name"] = type.Name,
            ["namespace"] = namespaceName,
            ["kind"] = type.TypeKind.ToString().ToLowerInvariant(),
            ["signature"] = typeSignature,
        };

        // XML doc summary
        if (!string.IsNullOrEmpty(typeSummary))
            result["summary"] = typeSummary;

        // Base type (for classes)
        if (type.BaseType is not null &&
            type.BaseType.SpecialType == SpecialType.None &&
            type.BaseType.Name != "Object")
        {
            result["baseType"] = type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        // Interfaces
        var interfaces = type.Interfaces
            .Where(i => i.DeclaredAccessibility == Accessibility.Public)
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();
        if (interfaces.Count > 0)
            result["interfaces"] = interfaces;

        if (!includeMembers)
        {
            if (!string.IsNullOrWhiteSpace(query) && !typeMatchesQuery)
                return null;

            return result;
        }

        // Public members (methods, properties)
        var members = new List<object>();
        var membersTruncated = false;
        foreach (var m in type.GetMembers())
        {
            if (m.DeclaredAccessibility != Accessibility.Public) continue;
            if (m.IsImplicitlyDeclared) continue;
            if (m is IMethodSymbol ms && ms.MethodKind is
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove)
                continue;

            var kind = SymbolFormatter.GetKind(m);
            if (!MatchesMemberKind(kind, memberKindFilter))
                continue;

            var signature = SymbolFormatter.GetSignature(m);
            var memberSummary = SymbolFormatter.GetXmlDocSummary(m);

            if (!MatchesAny(memberFilter, m.Name, signature, memberSummary))
                continue;

            var memberMatchesQuery = MatchesAny(query, m.Name, signature, memberSummary);
            if (!string.IsNullOrWhiteSpace(query) && !typeMatchesQuery && !memberMatchesQuery)
                continue;

            if (members.Count >= maxMembersPerType)
            {
                membersTruncated = true;
                break;
            }

            var memberEntry = new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["kind"] = kind,
                ["signature"] = signature,
            };

            if (!string.IsNullOrEmpty(memberSummary))
                memberEntry["summary"] = memberSummary;

            members.Add(memberEntry);
        }

        var hasStrictMemberFilters = !string.IsNullOrWhiteSpace(memberFilter)
            || !string.IsNullOrWhiteSpace(memberKindFilter);
        if (hasStrictMemberFilters && members.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(query) && !typeMatchesQuery && members.Count == 0)
            return null;

        if (members.Count > 0)
            result["members"] = members;
        if (membersTruncated)
            result["membersTruncated"] = true;

        return result;
    }

    private static bool MatchesAny(string? filter, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return values.Any(v => !string.IsNullOrWhiteSpace(v)
            && v.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesMemberKind(string memberKind, string? memberKindFilter)
    {
        if (string.IsNullOrWhiteSpace(memberKindFilter))
            return true;

        var normalizedMemberKind = NormalizeMemberKind(memberKind);
        var normalizedFilter = NormalizeMemberKind(memberKindFilter);
        return string.Equals(normalizedMemberKind, normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMemberKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "ctor" or "constructor") return "constructor";
        if (normalized is "prop" or "property") return "property";
        if (normalized is "meth" or "method") return "method";
        if (normalized is "fld" or "field") return "field";
        if (normalized is "evt" or "event") return "event";
        return normalized;
    }

    private static Dictionary<string, object> BuildAppliedFilters(
        string? namespaceFilter,
        string? kindFilter,
        string? query,
        string? typeFilter,
        string? memberFilter,
        string? memberKindFilter,
        bool includeMembers,
        int maxMembersPerType,
        int maxTypes)
    {
        var filters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(namespaceFilter)) filters["namespaceFilter"] = namespaceFilter;
        if (!string.IsNullOrWhiteSpace(kindFilter)) filters["kindFilter"] = kindFilter;
        if (!string.IsNullOrWhiteSpace(query)) filters["query"] = query;
        if (!string.IsNullOrWhiteSpace(typeFilter)) filters["typeFilter"] = typeFilter;
        if (!string.IsNullOrWhiteSpace(memberFilter)) filters["memberFilter"] = memberFilter;
        if (!string.IsNullOrWhiteSpace(memberKindFilter)) filters["memberKindFilter"] = memberKindFilter;
        if (!includeMembers) filters["includeMembers"] = includeMembers;
        if (maxMembersPerType != 200) filters["maxMembersPerType"] = maxMembersPerType;
        if (maxTypes != 200) filters["maxTypes"] = maxTypes;

        return filters;
    }

    private static async Task<string?> DetectInstalledVersionFromWorkspaceAsync(
        WorkspaceService workspace,
        string? solution,
        string packageName,
        CancellationToken ct)
    {
        try
        {
            using var workspaceSnapshot = await workspace.GetSnapshotAsync(solution, ct);
            var sln = workspaceSnapshot.Solution;
            var projectPaths = sln.Projects
                .Select(p => p.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var projectPath in projectPaths)
            {
                var explicitVersion = TryReadPackageVersionFromProject(projectPath, packageName);
                if (!string.IsNullOrWhiteSpace(explicitVersion))
                    return explicitVersion;
            }

            var propsCandidates = projectPaths
                .SelectMany(GetDirectoryPackagesPropsCandidates)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();

            foreach (var propsPath in propsCandidates)
            {
                var centralVersion = TryReadCentralPackageVersion(propsPath, packageName);
                if (!string.IsNullOrWhiteSpace(centralVersion))
                    return centralVersion;
            }
        }
        catch
        {
            // ignore and fall back to latest
        }

        return null;
    }

    private static string? TryReadPackageVersionFromProject(string projectPath, string packageName)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            var packageRef = document
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "PackageReference", StringComparison.Ordinal)
                    && string.Equals((string?)e.Attribute("Include"), packageName, StringComparison.OrdinalIgnoreCase));
            if (packageRef is null)
                return null;

            var versionAttr = (string?)packageRef.Attribute("Version");
            if (!string.IsNullOrWhiteSpace(versionAttr) && !LooksLikeMsBuildProperty(versionAttr))
                return versionAttr;

            var versionNode = packageRef
                .Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Version", StringComparison.Ordinal));
            var versionText = versionNode?.Value;
            if (!string.IsNullOrWhiteSpace(versionText) && !LooksLikeMsBuildProperty(versionText))
                return versionText;
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static IEnumerable<string> GetDirectoryPackagesPropsCandidates(string projectPath)
    {
        var current = Path.GetDirectoryName(projectPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return Path.Combine(current, "Directory.Packages.props");
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static string? TryReadCentralPackageVersion(string propsPath, string packageName)
    {
        try
        {
            var document = XDocument.Load(propsPath);
            var packageVersion = document
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "PackageVersion", StringComparison.Ordinal)
                    && string.Equals((string?)e.Attribute("Include"), packageName, StringComparison.OrdinalIgnoreCase));
            if (packageVersion is null)
                return null;

            var versionAttr = (string?)packageVersion.Attribute("Version");
            if (!string.IsNullOrWhiteSpace(versionAttr) && !LooksLikeMsBuildProperty(versionAttr))
                return versionAttr;
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static bool LooksLikeMsBuildProperty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("$(", StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal);
    }

    private static async Task<(int exitCode, string output)> RunDotnetAsync(string arguments, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var outTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                stdout.AppendLine(line);
        }, ct);

        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
                stderr.AppendLine(line);
        }, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outTask, errTask);

        var output = stdout.ToString();
        if (stderr.Length > 0)
            output += "\n--- stderr ---\n" + stderr;

        return (process.ExitCode, output);
    }

    private static string? DetectInstalledVersion(string tempDir, string packageName)
    {
        // Try to find the version from the obj/project.assets.json
        var assetsPath = Path.Combine(tempDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath)) return null;

        try
        {
            var json = File.ReadAllText(assetsPath);
            // Simple pattern match: "PackageName/1.2.3"
            var pattern = $"\"{packageName}/";
            var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var start = idx + pattern.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return null;

            return json[start..end];
        }
        catch { return null; }
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string TruncateOutput(string output, int maxLength) =>
        output.Length <= maxLength ? output : output[..maxLength] + "... (truncated)";
}
