using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Services;

internal sealed record ImpactSection<T>(
    int Returned,
    int? Total,
    bool Truncated,
    IReadOnlyList<T> Items);

internal sealed record ReferenceImpact(
    string Kind,
    string Definition,
    SourceEvidence Evidence);

internal sealed record CallImpact(
    int Depth,
    string Kind,
    bool? IsDirect,
    SymbolCandidate From,
    SymbolCandidate To,
    SourceEvidence Evidence);

internal sealed record SymbolImpactResult(
    SymbolCandidate Symbol,
    int MaxDepth,
    int MaxResultsPerSection,
    ImpactSection<ReferenceImpact> References,
    ImpactSection<CallImpact> IncomingCalls,
    ImpactSection<CallImpact> OutgoingCalls,
    ImpactSection<SymbolCandidate> Implementations,
    ImpactSection<SymbolCandidate> Overrides,
    int TestReferenceCount,
    IReadOnlyList<string> Limitations);

internal static class SymbolImpactAnalyzer
{
    public static async Task<SymbolImpactResult> AnalyzeAsync(
        Solution solution,
        ResolvedSymbol target,
        int depth,
        int maxResultsPerSection,
        CancellationToken ct)
    {
        depth = Math.Clamp(depth, 1, 3);
        maxResultsPerSection = Math.Clamp(maxResultsPerSection, 1, 500);

        var (references, testReferenceCount) = await FindReferencesAsync(
            solution,
            target,
            maxResultsPerSection,
            ct);
        var incomingCalls = await FindIncomingCallsAsync(
            solution,
            target,
            depth,
            maxResultsPerSection,
            ct);
        var outgoingCalls = await FindOutgoingCallsAsync(
            solution,
            target,
            depth,
            maxResultsPerSection,
            ct);
        var (implementations, overrides) = await FindImplementationsAndOverridesAsync(
            solution,
            target,
            maxResultsPerSection,
            ct);

        return new SymbolImpactResult(
            SymbolResolver.Describe(solution, target.Symbol, target.Project),
            depth,
            maxResultsPerSection,
            references,
            incomingCalls,
            outgoingCalls,
            implementations,
            overrides,
            testReferenceCount,
            [
                "Results describe saved files in the reported workspace snapshot; unsaved editor buffers are not visible.",
                "Call edges are static evidence. Reflection, dynamic dispatch, delegates, dependency-injection activation, and runtime configuration may add behavior not represented here.",
                "Virtual/interface calls may have multiple runtime targets; IsDirect only reflects Roslyn's static caller relationship.",
                "Test-project classification is reported only when the project name or path makes that evidence explicit.",
            ]);
    }

    private static async Task<(ImpactSection<ReferenceImpact> Section, int TestReferenceCount)>
        FindReferencesAsync(
        Solution solution,
        ResolvedSymbol target,
        int maxResults,
        CancellationToken ct)
    {
        var groups = await SymbolFinder.FindReferencesAsync(target.Symbol, solution, ct);
        var items = new List<ReferenceImpact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var testReferenceCount = 0;

        foreach (var group in groups)
        {
            foreach (var reference in group.Locations)
            {
                var location = reference.Location;
                if (!location.IsInSource)
                    continue;

                var key = $"{reference.Document.Id.Id:N}:{GetLocationIdentity(location)}";
                if (!seen.Add(key))
                    continue;

                total++;
                if (SemanticEvidence.GetTestProjectEvidence(reference.Document.Project) is not null)
                    testReferenceCount++;
                if (items.Count >= maxResults)
                    continue;

                var evidence = await SemanticEvidence.CreateAsync(
                    solution,
                    location,
                    reference.Document,
                    ct);

                items.Add(new ReferenceImpact(
                    await ClassifyReferenceAsync(reference, target.Symbol, ct),
                    SymbolFormatter.GetFullyQualifiedName(group.Definition),
                    evidence));
            }
        }

        return (
            new ImpactSection<ReferenceImpact>(
                items.Count,
                total,
                total > items.Count,
                items),
            testReferenceCount);
    }

    private static async Task<string> ClassifyReferenceAsync(
        ReferenceLocation reference,
        ISymbol target,
        CancellationToken ct)
    {
        if (reference.IsImplicit)
            return "implicit";

        var root = await reference.Document.GetSyntaxRootAsync(ct);
        if (root is null)
            return "reference";

        var node = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
        if (IsWriteReference(node, reference.Location.SourceSpan))
            return "write";

        if (IsConstructedTypeReference(node, reference.Location.SourceSpan))
            return "construction";

        if (IsInvokedExpressionReference(node, reference.Location.SourceSpan))
            return "invocation";

        return target is INamedTypeSymbol ? "type-use" : "reference";
    }

    private static bool IsConstructedTypeReference(SyntaxNode node, TextSpan referenceSpan)
    {
        foreach (var creation in node.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>())
        {
            TextSpan? constructedNameSpan = creation.Type switch
            {
                QualifiedNameSyntax qualifiedName => qualifiedName.Right.Identifier.Span,
                AliasQualifiedNameSyntax aliasQualifiedName => aliasQualifiedName.Name.Identifier.Span,
                SimpleNameSyntax simpleName => simpleName.Identifier.Span,
                PredefinedTypeSyntax predefinedType => predefinedType.Keyword.Span,
                _ => null,
            };
            if (constructedNameSpan?.Contains(referenceSpan) == true)
                return true;
        }

        return false;
    }

    private static bool IsInvokedExpressionReference(SyntaxNode node, TextSpan referenceSpan)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var expression = invocation.Expression;
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;

            var invokedName = expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
                SimpleNameSyntax simpleName => simpleName,
                _ => null,
            };
            if (invokedName?.Identifier.Span.Contains(referenceSpan) == true)
                return true;
        }

        return false;
    }

    private static bool IsWriteReference(SyntaxNode node, TextSpan referenceSpan)
    {
        var assignment = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assignment is not null && assignment.Left.Span.Contains(referenceSpan))
            return true;

        var argument = node.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argument?.RefKindKeyword.Kind() is
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword
            or Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)
        {
            return true;
        }

        var prefix = node.AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>().FirstOrDefault();
        if (prefix?.Kind() is
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression
            or Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression)
        {
            return true;
        }

        var postfix = node.AncestorsAndSelf().OfType<PostfixUnaryExpressionSyntax>().FirstOrDefault();
        return postfix?.Kind() is
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression
            or Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression;
    }

    private static async Task<ImpactSection<CallImpact>> FindIncomingCallsAsync(
        Solution solution,
        ResolvedSymbol target,
        int maxDepth,
        int maxResults,
        CancellationToken ct)
    {
        if (!CanHaveCallers(target.Symbol))
            return new ImpactSection<CallImpact>(0, 0, false, []);

        var results = new List<CallImpact>();
        var queue = new Queue<(ResolvedSymbol Symbol, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            SymbolResolver.GetIdentity(target.Symbol, target.Project),
        };
        var truncated = false;
        queue.Enqueue((target, 0));

        while (queue.Count > 0 && !truncated)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth)
                continue;

            var callers = await SymbolFinder.FindCallersAsync(current.Symbol.Symbol, solution, ct);
            foreach (var caller in callers)
            {
                var callerProject = await SymbolResolver.FindProjectForSymbolAsync(
                    solution,
                    caller.CallingSymbol,
                    current.Symbol.Project,
                    ct);
                if (callerProject is null)
                    continue;

                foreach (var location in caller.Locations.Where(location => location.IsInSource))
                {
                    if (results.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    var evidence = await SemanticEvidence.CreateAsync(
                        solution,
                        location,
                        callerProject,
                        ct);
                    if (evidence is null)
                        continue;

                    results.Add(new CallImpact(
                        current.Depth + 1,
                        "invocation",
                        caller.IsDirect,
                        SymbolResolver.Describe(solution, caller.CallingSymbol, callerProject),
                        SymbolResolver.Describe(solution, current.Symbol.Symbol, current.Symbol.Project),
                        evidence));
                }

                if (current.Depth + 1 < maxDepth
                    && caller.CallingSymbol.Locations.Any(location => location.IsInSource))
                {
                    var identity = SymbolResolver.GetIdentity(caller.CallingSymbol, callerProject);
                    if (visited.Add(identity))
                        queue.Enqueue((new ResolvedSymbol(caller.CallingSymbol, callerProject), current.Depth + 1));
                }
            }
        }

        return new ImpactSection<CallImpact>(
            results.Count,
            truncated ? null : results.Count,
            truncated,
            results);
    }

    private static async Task<ImpactSection<CallImpact>> FindOutgoingCallsAsync(
        Solution solution,
        ResolvedSymbol target,
        int maxDepth,
        int maxResults,
        CancellationToken ct)
    {
        var results = new List<CallImpact>();
        var queue = new Queue<(ResolvedSymbol Symbol, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            SymbolResolver.GetIdentity(target.Symbol, target.Project),
        };
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        queue.Enqueue((target, 0));

        while (queue.Count > 0 && !truncated)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth)
                continue;

            var outgoing = await GetOutgoingCallsAsync(solution, current.Symbol, ct);
            foreach (var call in outgoing)
            {
                var calledProject = await SymbolResolver.FindProjectForSymbolAsync(
                    solution,
                    call.CalledSymbol,
                    current.Symbol.Project,
                    ct);
                if (calledProject is null)
                    calledProject = current.Symbol.Project;

                var edgeKey = $"{SymbolResolver.GetIdentity(current.Symbol.Symbol, current.Symbol.Project)}"
                    + $"->{SymbolResolver.GetIdentity(call.CalledSymbol, calledProject)}"
                    + $":{current.Symbol.Project.Id.Id:N}:{GetLocationIdentity(call.Location)}";
                if (!edgeKeys.Add(edgeKey))
                    continue;

                if (results.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                var evidence = await SemanticEvidence.CreateAsync(
                    solution,
                    call.Location,
                    current.Symbol.Project,
                    ct);
                if (evidence is null)
                    continue;

                results.Add(new CallImpact(
                    current.Depth + 1,
                    call.Kind,
                    null,
                    SymbolResolver.Describe(solution, current.Symbol.Symbol, current.Symbol.Project),
                    SymbolResolver.Describe(solution, call.CalledSymbol, calledProject),
                    evidence));

                if (current.Depth + 1 < maxDepth
                    && call.CalledSymbol.Locations.Any(location => location.IsInSource))
                {
                    var identity = SymbolResolver.GetIdentity(call.CalledSymbol, calledProject);
                    if (visited.Add(identity))
                        queue.Enqueue((new ResolvedSymbol(call.CalledSymbol, calledProject), current.Depth + 1));
                }
            }
        }

        return new ImpactSection<CallImpact>(
            results.Count,
            truncated ? null : results.Count,
            truncated,
            results);
    }

    private static async Task<List<OutgoingCall>> GetOutgoingCallsAsync(
        Solution solution,
        ResolvedSymbol source,
        CancellationToken ct)
    {
        var results = new List<OutgoingCall>();

        foreach (var syntaxReference in source.Symbol.DeclaringSyntaxReferences)
        {
            var syntax = await syntaxReference.GetSyntaxAsync(ct);
            var document = SemanticEvidence.FindDocument(
                solution,
                syntax.SyntaxTree.FilePath,
                source.Project);
            if (document is null)
                continue;

            var semanticModel = await document.GetSemanticModelAsync(ct);
            if (semanticModel is null)
                continue;

            foreach (var invocation in syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (IsInAnalyzedExecutableScope(semanticModel, invocation, source.Symbol, ct)
                    && semanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol method)
                {
                    results.Add(new OutgoingCall(
                        method.ReducedFrom ?? method,
                        invocation.GetLocation(),
                        "invocation"));
                }
            }

            foreach (var creation in syntax.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
            {
                if (IsInAnalyzedExecutableScope(semanticModel, creation, source.Symbol, ct)
                    && semanticModel.GetSymbolInfo(creation, ct).Symbol is IMethodSymbol constructor)
                    results.Add(new OutgoingCall(constructor, creation.GetLocation(), "construction"));
            }

            foreach (var creation in syntax.DescendantNodesAndSelf().OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                if (IsInAnalyzedExecutableScope(semanticModel, creation, source.Symbol, ct)
                    && semanticModel.GetSymbolInfo(creation, ct).Symbol is IMethodSymbol constructor)
                    results.Add(new OutgoingCall(constructor, creation.GetLocation(), "construction"));
            }

            foreach (var initializer in syntax.DescendantNodesAndSelf().OfType<ConstructorInitializerSyntax>())
            {
                if (IsInAnalyzedExecutableScope(semanticModel, initializer, source.Symbol, ct)
                    && semanticModel.GetSymbolInfo(initializer, ct).Symbol is IMethodSymbol constructor)
                {
                    results.Add(new OutgoingCall(
                        constructor,
                        initializer.GetLocation(),
                        "constructor-initializer"));
                }
            }
        }

        return results;
    }

    private static bool IsInAnalyzedExecutableScope(
        SemanticModel semanticModel,
        SyntaxNode node,
        ISymbol analyzedSymbol,
        CancellationToken ct)
    {
        var enclosingSymbol = semanticModel.GetEnclosingSymbol(node.SpanStart, ct);
        if (SymbolEqualityComparer.Default.Equals(enclosingSymbol, analyzedSymbol))
            return true;

        return enclosingSymbol is IMethodSymbol { AssociatedSymbol: { } associatedSymbol }
            && SymbolEqualityComparer.Default.Equals(associatedSymbol, analyzedSymbol);
    }

    private static async Task<(
        ImpactSection<SymbolCandidate> Implementations,
        ImpactSection<SymbolCandidate> Overrides)> FindImplementationsAndOverridesAsync(
        Solution solution,
        ResolvedSymbol target,
        int maxResults,
        CancellationToken ct)
    {
        var implementations = new List<ISymbol>();
        var overrides = new List<ISymbol>();

        if (target.Symbol is INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                implementations.AddRange(
                    await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct));
            }
            else
            {
                implementations.AddRange(
                    await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: ct));
            }
        }
        else if (target.Symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            overrides.AddRange(
                await SymbolFinder.FindOverridesAsync(target.Symbol, solution, cancellationToken: ct));

            if (target.Symbol.ContainingType?.TypeKind == TypeKind.Interface)
            {
                implementations.AddRange(
                    await SymbolFinder.FindImplementationsAsync(
                        target.Symbol,
                        solution,
                        cancellationToken: ct));
            }
        }

        var implementationCandidates = await DescribeDistinctSymbolsAsync(
            solution,
            implementations,
            target.Project,
            ct);
        var overrideCandidates = await DescribeDistinctSymbolsAsync(
            solution,
            overrides,
            target.Project,
            ct);

        return (
            CreateSection(implementationCandidates, maxResults),
            CreateSection(overrideCandidates, maxResults));
    }

    private static async Task<List<SymbolCandidate>> DescribeDistinctSymbolsAsync(
        Solution solution,
        IEnumerable<ISymbol> symbols,
        Project fallbackProject,
        CancellationToken ct)
    {
        var results = new List<SymbolCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            var project = await SymbolResolver.FindProjectForSymbolAsync(
                    solution,
                    symbol,
                    fallbackProject,
                    ct)
                ?? fallbackProject;
            var identity = SymbolResolver.GetIdentity(symbol, project);
            if (seen.Add(identity))
                results.Add(SymbolResolver.Describe(solution, symbol, project));
        }

        return results;
    }

    private static ImpactSection<T> CreateSection<T>(IReadOnlyList<T> all, int maxResults)
    {
        var items = all.Take(maxResults).ToList();
        return new ImpactSection<T>(
            items.Count,
            all.Count,
            all.Count > maxResults,
            items);
    }

    private static bool CanHaveCallers(ISymbol symbol) =>
        symbol is IMethodSymbol or IPropertySymbol or IEventSymbol;

    private static string GetLocationIdentity(Location location)
    {
        var span = location.GetLineSpan();
        return $"{span.Path}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
    }

    private sealed record OutgoingCall(ISymbol CalledSymbol, Location Location, string Kind);
}
