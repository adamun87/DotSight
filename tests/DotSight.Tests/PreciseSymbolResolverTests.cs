using DotSight.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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

    [Fact]
    public async Task ResolveAsync_UsesFormatterNamesForMetadataSymbols()
    {
        using var workspace = new AdhocWorkspace();
        var libraryId = ProjectId.CreateNewId("Library");
        var consumerId = ProjectId.CreateNewId("Consumer");
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(
                    Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                    "System.Runtime.dll")),
        };
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libraryId,
                VersionStamp.Create(),
                "Library",
                "Library",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddProject(ProjectInfo.Create(
                consumerId,
                VersionStamp.Create(),
                "Consumer",
                "Consumer",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddProjectReference(consumerId, new ProjectReference(libraryId))
            .AddDocument(
                DocumentId.CreateNewId(libraryId),
                "Library.cs",
                SourceText.From(
                    """
                    namespace Probe;
                    public class Box<T> { public void Put(T value) { } }
                    public class Outer
                    {
                        public class Inner { public void Act(int value) { } }
                    }
                    """))
            .AddDocument(
                DocumentId.CreateNewId(consumerId),
                "Consumer.cs",
                SourceText.From("namespace ConsumerCode; public class Marker { }"));
        Assert.True(workspace.TryApplyChanges(solution));
        solution = workspace.CurrentSolution;

        var compilation = await solution.GetProject(consumerId)!
            .GetCompilationAsync(TestContext.Current.CancellationToken);
        var genericType = compilation!.GetTypeByMetadataName("Probe.Box`1")!;
        var nestedType = compilation.GetTypeByMetadataName("Probe.Outer+Inner")!;
        var symbols = new ISymbol[]
        {
            genericType,
            genericType.GetMembers("Put").Single(),
            nestedType.GetMembers("Act").Single(),
            compilation.GetSpecialType(SpecialType.System_Int32),
        };

        foreach (var symbol in symbols)
        {
            var formattedName = SymbolFormatter.GetFullyQualifiedName(symbol);
            var result = await SymbolResolver.ResolveAsync(
                solution,
                new SymbolSelector(formattedName, Project: "Consumer"),
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, $"Could not resolve '{formattedName}': {result.Error}");
            Assert.Equal(
                formattedName,
                SymbolFormatter.GetFullyQualifiedName(result.Match!.Symbol));
        }
    }
}
