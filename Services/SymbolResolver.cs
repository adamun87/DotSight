using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotSight.Services;

internal sealed record SymbolSelector(
    string? FullyQualifiedName = null,
    string? Signature = null,
    string? Project = null,
    string? File = null,
    int? Line = null,
    int? Column = null);

internal sealed record ResolvedSymbol(ISymbol Symbol, Project Project);

internal sealed record SymbolCandidate(
    string Name,
    string Kind,
    string FullyQualifiedName,
    string Signature,
    string Project,
    string Origin,
    string? File,
    int? Line,
    int? Column);

internal sealed record SymbolResolutionResult(
    ResolvedSymbol? Match,
    IReadOnlyList<SymbolCandidate> Candidates,
    string? Error)
{
    public bool Succeeded => Match is not null;

    public object ToErrorPayload() => new
    {
        error = Error,
        candidates = Candidates,
        hint = Candidates.Count > 1
            ? "Provide project and signature, or select the symbol by file, line, and column."
            : null,
    };
}

internal static partial class SymbolResolver
{
    public static async Task<SymbolResolutionResult> ResolveAsync(
        Solution solution,
        SymbolSelector selector,
        CancellationToken ct)
    {
        var hasAnyPosition = selector.File is not null || selector.Line is not null || selector.Column is not null;
        var hasCompletePosition = selector.File is not null && selector.Line is not null && selector.Column is not null;

        if (hasAnyPosition && !hasCompletePosition)
        {
            return Failure(
                "Source-position selection requires file, line, and column together.");
        }

        if (hasCompletePosition)
            return await ResolveAtPositionAsync(solution, selector, ct);

        if (string.IsNullOrWhiteSpace(selector.FullyQualifiedName))
        {
            return Failure(
                "Provide fullyQualifiedName, or select the symbol by file, line, and column.");
        }

        return await ResolveByNameAsync(solution, selector, ct);
    }

    public static SymbolCandidate Describe(
        Solution solution,
        ISymbol symbol,
        Project project)
    {
        var solutionDirectory = GetSolutionDirectory(solution);
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        string? file = null;
        int? line = null;
        int? column = null;

        if (sourceLocation is not null)
        {
            var span = sourceLocation.GetLineSpan();
            file = MakeRelativePath(solutionDirectory, span.Path);
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }

        return new SymbolCandidate(
            symbol.Name,
            SymbolFormatter.GetKind(symbol),
            SymbolFormatter.GetFullyQualifiedName(symbol),
            SymbolFormatter.GetSignature(symbol),
            project.Name,
            GetOrigin(symbol, file),
            file,
            line,
            column);
    }

    public static string GetIdentity(ISymbol symbol, Project project)
    {
        var documentationId = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrWhiteSpace(documentationId))
            return $"{project.Id.Id:N}:{documentationId}";

        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (location is not null)
        {
            var span = location.GetLineSpan();
            return $"{project.Id.Id:N}:{span.Path}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
        }

        return $"{project.Id.Id:N}:{symbol.Kind}:{SymbolFormatter.GetFullyQualifiedName(symbol)}";
    }

    public static async Task<Project?> FindProjectForSymbolAsync(
        Solution solution,
        ISymbol symbol,
        Project? preferredProject,
        CancellationToken ct)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (sourceLocation is null)
            return preferredProject;

        var candidateProjects = solution
            .GetDocumentIdsWithFilePath(sourceLocation.GetLineSpan().Path)
            .Select(documentId => solution.GetProject(documentId.ProjectId))
            .Where(project => project is not null)
            .Cast<Project>()
            .DistinctBy(project => project.Id)
            .ToList();
        if (candidateProjects.Count == 0)
            return preferredProject;
        if (candidateProjects.Count == 1)
            return candidateProjects[0];

        if (symbol.ContainingAssembly is not null)
        {
            foreach (var project in candidateProjects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is not null
                    && SymbolEqualityComparer.Default.Equals(
                        compilation.Assembly,
                        symbol.ContainingAssembly))
                {
                    return project;
                }
            }
        }

        return preferredProject is not null
            && candidateProjects.Any(project => project.Id == preferredProject.Id)
                ? preferredProject
                : candidateProjects[0];
    }

    private static async Task<SymbolResolutionResult> ResolveAtPositionAsync(
        Solution solution,
        SymbolSelector selector,
        CancellationToken ct)
    {
        var solutionDirectory = GetSolutionDirectory(solution);
        var requestedPath = Path.IsPathRooted(selector.File!)
            ? Path.GetFullPath(selector.File!)
            : Path.GetFullPath(Path.Combine(solutionDirectory, selector.File!));

        var projects = FilterProjects(solution, selector.Project).ToList();
        if (projects.Count == 0)
            return Failure($"Project '{selector.Project}' was not found.");

        var matches = new List<ResolvedSymbol>();
        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document =>
                         document.FilePath is not null
                         && string.Equals(
                             Path.GetFullPath(document.FilePath),
                             requestedPath,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var text = await document.GetTextAsync(ct);
                var lineIndex = selector.Line!.Value - 1;
                if (lineIndex < 0 || lineIndex >= text.Lines.Count)
                {
                    return Failure(
                        $"Line {selector.Line} is outside '{selector.File}' ({text.Lines.Count} lines).");
                }

                var textLine = text.Lines[lineIndex];
                var columnOffset = selector.Column!.Value - 1;
                if (columnOffset < 0 || columnOffset > textLine.Span.Length)
                {
                    return Failure(
                        $"Column {selector.Column} is outside line {selector.Line} of '{selector.File}'.");
                }

                var semanticModel = await document.GetSemanticModelAsync(ct);
                if (semanticModel is null)
                    continue;

                var position = textLine.Start + columnOffset;
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
                    semanticModel,
                    position,
                    solution.Workspace,
                    ct);
                symbol ??= await FindSymbolFromSyntaxAsync(document, semanticModel, position, ct);
                if (symbol is IAliasSymbol alias)
                    symbol = alias.Target;
                if (symbol is not null)
                    matches.Add(new ResolvedSymbol(symbol, project));
            }
        }

        return FinalizeMatches(
            solution,
            matches,
            $"No symbol was found at {selector.File}({selector.Line},{selector.Column}).");
    }

    private static async Task<ISymbol?> FindSymbolFromSyntaxAsync(
        Document document,
        SemanticModel semanticModel,
        int position,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null)
            return null;

        var token = root.FindToken(Math.Min(position, Math.Max(0, root.FullSpan.End - 1)));
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = semanticModel.GetDeclaredSymbol(node, ct);
            if (declared is not null)
                return declared;

            var referenced = semanticModel.GetSymbolInfo(node, ct).Symbol;
            if (referenced is not null)
                return referenced;
        }

        return null;
    }

    private static async Task<SymbolResolutionResult> ResolveByNameAsync(
        Solution solution,
        SymbolSelector selector,
        CancellationToken ct)
    {
        var projects = FilterProjects(solution, selector.Project).ToList();
        if (projects.Count == 0)
            return Failure($"Project '{selector.Project}' was not found.");

        var query = NormalizeName(selector.FullyQualifiedName!);
        var matches = new List<ResolvedSymbol>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            foreach (var symbol in EnumerateSourceSymbols(compilation.Assembly.GlobalNamespace))
            {
                if (MatchesName(symbol, query) && MatchesSignature(symbol, selector.Signature))
                    matches.Add(new ResolvedSymbol(symbol, project));
            }
        }

        if (matches.Count == 0)
        {
            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                    continue;

                foreach (var symbol in ResolveMetadataCandidates(compilation, query))
                {
                    if (MatchesSignature(symbol, selector.Signature))
                        matches.Add(new ResolvedSymbol(symbol, project));
                }
            }
        }

        return FinalizeMatches(
            solution,
            matches,
            $"Symbol '{selector.FullyQualifiedName}' was not found in the selected project scope.");
    }

    private static SymbolResolutionResult FinalizeMatches(
        Solution solution,
        IEnumerable<ResolvedSymbol> matches,
        string notFoundMessage)
    {
        var unique = matches
            .GroupBy(match => GetIdentity(match.Symbol, match.Project), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (unique.Count == 1)
            return new SymbolResolutionResult(unique[0], [], null);

        if (unique.Count == 0)
            return Failure(notFoundMessage);

        var candidates = unique
            .Select(match => Describe(solution, match.Symbol, match.Project))
            .OrderBy(candidate => candidate.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .ToList();
        return new SymbolResolutionResult(
            null,
            candidates,
            $"The selector matched {candidates.Count} symbols.");
    }

    private static IEnumerable<Project> FilterProjects(Solution solution, string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
            return solution.Projects;

        return solution.Projects.Where(candidate =>
            string.Equals(candidate.Name, project, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                if (!childNamespace.IsGlobalNamespace && childNamespace.Locations.Any(location => location.IsInSource))
                    yield return childNamespace;

                foreach (var nested in EnumerateSourceSymbols(childNamespace))
                    yield return nested;
                continue;
            }

            if (member is not INamedTypeSymbol type || !type.Locations.Any(location => location.IsInSource))
                continue;

            foreach (var symbol in EnumerateTypeAndMembers(type))
                yield return symbol;
        }
    }

    private static IEnumerable<ISymbol> EnumerateTypeAndMembers(INamedTypeSymbol type)
    {
        yield return type;

        foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
        {
            yield return member;
            if (member is INamedTypeSymbol nestedType)
            {
                foreach (var nested in EnumerateTypeAndMembers(nestedType))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<ISymbol> ResolveMetadataCandidates(
        Compilation compilation,
        string query)
    {
        var nameWithoutParameters = RemoveParameterList(query);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var type in ResolveMetadataTypes(compilation, nameWithoutParameters))
        {
            if (MatchesName(type, query) && seen.Add(type))
                yield return type;
        }

        foreach (var separator in GetTopLevelDotIndexes(nameWithoutParameters).Reverse())
        {
            var containingTypeName = nameWithoutParameters[..separator];
            foreach (var containingType in ResolveMetadataTypes(compilation, containingTypeName))
            {
                foreach (var member in containingType.GetMembers())
                {
                    if (MatchesName(member, query) && seen.Add(member))
                        yield return member;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> ResolveMetadataTypes(
        Compilation compilation,
        string displayName)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var specialTypeName = GetSpecialTypeMetadataName(displayName);
        if (specialTypeName is not null)
        {
            foreach (var type in compilation.GetTypesByMetadataName(specialTypeName))
            {
                if (seen.Add(type))
                    yield return type;
            }
        }

        var segments = SplitTypeName(displayName);
        for (var topLevelTypeIndex = segments.Count - 1;
             topLevelTypeIndex >= 0;
             topLevelTypeIndex--)
        {
            var metadataName = string.Join(
                ".",
                segments
                    .Take(topLevelTypeIndex + 1)
                    .Select(ToMetadataNameSegment));
            if (topLevelTypeIndex + 1 < segments.Count)
            {
                metadataName += "+"
                    + string.Join(
                        "+",
                        segments
                            .Skip(topLevelTypeIndex + 1)
                            .Select(ToMetadataNameSegment));
            }

            foreach (var type in compilation.GetTypesByMetadataName(metadataName))
            {
                if (seen.Add(type))
                    yield return type;
            }
        }
    }

    private static List<string> SplitTypeName(string displayName)
    {
        var segments = new List<string>();
        var segmentStart = 0;
        var genericDepth = 0;
        for (var index = 0; index < displayName.Length; index++)
        {
            switch (displayName[index])
            {
                case '<':
                    genericDepth++;
                    break;
                case '>':
                    genericDepth = Math.Max(0, genericDepth - 1);
                    break;
                case '.' when genericDepth == 0:
                    segments.Add(displayName[segmentStart..index]);
                    segmentStart = index + 1;
                    break;
            }
        }

        segments.Add(displayName[segmentStart..]);
        return segments
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToList();
    }

    private static IEnumerable<int> GetTopLevelDotIndexes(string value)
    {
        var genericDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<':
                    genericDepth++;
                    break;
                case '>':
                    genericDepth = Math.Max(0, genericDepth - 1);
                    break;
                case '.' when genericDepth == 0:
                    yield return index;
                    break;
            }
        }
    }

    private static string ToMetadataNameSegment(string segment)
    {
        segment = segment.Trim().TrimEnd('?');
        if (segment.StartsWith('@'))
            segment = segment[1..];

        var genericStart = segment.IndexOf('<');
        if (genericStart < 0)
            return segment;

        var arity = 1;
        var genericDepth = 0;
        for (var index = genericStart + 1; index < segment.Length; index++)
        {
            switch (segment[index])
            {
                case '<':
                    genericDepth++;
                    break;
                case '>':
                    if (genericDepth == 0)
                        return $"{segment[..genericStart]}`{arity}";
                    genericDepth--;
                    break;
                case ',' when genericDepth == 0:
                    arity++;
                    break;
            }
        }

        return segment;
    }

    private static string? GetSpecialTypeMetadataName(string displayName) =>
        displayName.Trim().TrimEnd('?') switch
        {
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "nint" => "System.IntPtr",
            "nuint" => "System.UIntPtr",
            "char" => "System.Char",
            "float" => "System.Single",
            "double" => "System.Double",
            "decimal" => "System.Decimal",
            "string" => "System.String",
            "object" => "System.Object",
            "void" => "System.Void",
            _ => null,
        };

    private static bool MatchesName(ISymbol symbol, string query)
    {
        var fullyQualifiedName = NormalizeName(SymbolFormatter.GetFullyQualifiedName(symbol));
        if (string.Equals(fullyQualifiedName, query, StringComparison.Ordinal))
            return true;

        if (string.Equals(RemoveParameterList(fullyQualifiedName), RemoveParameterList(query), StringComparison.Ordinal))
            return true;

        var errorMessageName = NormalizeName(
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return string.Equals(errorMessageName, query, StringComparison.Ordinal)
            || string.Equals(RemoveParameterList(errorMessageName), RemoveParameterList(query), StringComparison.Ordinal);
    }

    private static bool MatchesSignature(ISymbol symbol, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return true;

        var requested = NormalizeSignature(signature);
        var signatures = new[]
        {
            SymbolFormatter.GetSignature(symbol),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            SymbolFormatter.GetFullyQualifiedName(symbol),
        };

        return signatures
            .Select(NormalizeSignature)
            .Any(candidate =>
                string.Equals(candidate, requested, StringComparison.Ordinal)
                || candidate.EndsWith(requested, StringComparison.Ordinal));
    }

    private static string GetOrigin(ISymbol symbol, string? relativeFile)
    {
        if (!symbol.Locations.Any(location => location.IsInSource))
            return "metadata";

        if (relativeFile is not null)
        {
            var normalized = relativeFile.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                return "generated";
            }
        }

        return "source";
    }

    private static string GetSolutionDirectory(Solution solution)
    {
        var path = solution.FilePath ?? solution.Projects.Select(project => project.FilePath).FirstOrDefault(path => path is not null);
        return path is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
    }

    private static string MakeRelativePath(string solutionDirectory, string path) =>
        Path.IsPathRooted(path) ? Path.GetRelativePath(solutionDirectory, path) : path;

    private static string NormalizeName(string value) =>
        value.Replace("global::", "", StringComparison.Ordinal).Trim();

    private static string RemoveParameterList(string value)
    {
        var parameterStart = value.IndexOf('(');
        return parameterStart < 0 ? value : value[..parameterStart];
    }

    private static string NormalizeSignature(string value) =>
        SignatureWhitespaceRegex().Replace(value.Trim(), " ");

    private static SymbolResolutionResult Failure(string error) =>
        new(null, [], error);

    [GeneratedRegex(@"\s+")]
    private static partial Regex SignatureWhitespaceRegex();
}
