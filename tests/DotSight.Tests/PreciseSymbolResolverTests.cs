using DotSight.Services;
using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

public sealed class PreciseSymbolResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsOverloadCandidatesInsteadOfPickingFirst()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector("Demo.Worker.Run", Project: "Demo"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal("Run", candidate.Name));
    }

    [Fact]
    public async Task ResolveAsync_UsesSignatureToSelectOneOverload()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector(
                "Demo.Worker.Run",
                "public void Run(string value)",
                "Demo"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(result.Match!.Symbol);
        Assert.Equal(SpecialType.System_String, method.Parameters.Single().Type.SpecialType);
    }

    [Fact]
    public async Task ResolveAsync_SelectsInvocationBySourcePosition()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var document = testSolution.Solution.GetProject(testSolution.AppProjectId)!.Documents.Single();
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        var callStart = text.ToString().IndexOf("Run(\"job\")", StringComparison.Ordinal);
        var position = text.Lines.GetLinePosition(callStart + 1);

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector(
                Project: "Demo",
                File: "src\\Demo\\Workflow.cs",
                Line: position.Line + 1,
                Column: position.Character + 1),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(result.Match!.Symbol);
        Assert.Equal("Run", method.Name);
        Assert.Equal(SpecialType.System_String, method.Parameters.Single().Type.SpecialType);
    }
}
