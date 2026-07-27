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

    [Fact]
    public async Task ResolveAsync_CollapsesEquivalentMetadataSymbolsAcrossProjects()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector("System.Collections.Generic.List<T>"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("metadata", SymbolResolver.Describe(
            testSolution.Solution,
            result.Match!.Symbol,
            result.Match.Project).Origin);
    }

    [Fact]
    public async Task ResolveAsync_SuggestsTypeParametersForGenericMetadataType()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector("System.Collections.Generic.List"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("System.Collections.Generic.List<T>", result.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_UsesFormattedNameForIndexer()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var project = testSolution.Solution.GetProject(testSolution.AppProjectId)!;
        var compilation = await project.GetCompilationAsync(
            TestContext.Current.CancellationToken);
        var indexer = compilation!
            .GetTypeByMetadataName("Demo.Worker")!
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Single(property => property.IsIndexer);
        var formattedName = SymbolFormatter.GetFullyQualifiedName(indexer);

        var result = await SymbolResolver.ResolveAsync(
            testSolution.Solution,
            new SymbolSelector(formattedName, Project: "Demo"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Assert.IsAssignableFrom<IPropertySymbol>(result.Match!.Symbol).IsIndexer);
    }

    [Fact]
    public async Task ResolveAsync_UsesFormattedNamesForExplicitAccessors()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var documentId = DocumentId.CreateNewId(testSolution.AppProjectId);
        var solution = testSolution.Solution.AddDocument(
            documentId,
            "Accessors.cs",
            SourceText.From(
                """
                namespace Demo;

                public interface IAccessors
                {
                    int Value { get; }
                    event System.Action Changed;
                }

                public sealed class Accessors : IAccessors
                {
                    int IAccessors.Value => 0;
                    event System.Action IAccessors.Changed
                    {
                        add { }
                        remove { }
                    }
                }
                """),
            filePath: Path.Combine(testSolution.RootDirectory, "src", "Demo", "Accessors.cs"));
        var project = solution.GetProject(testSolution.AppProjectId)!;
        var compilation = await project.GetCompilationAsync(
            TestContext.Current.CancellationToken);
        var type = compilation!.GetTypeByMetadataName("Demo.Accessors")!;
        var property = type.GetMembers().OfType<IPropertySymbol>().Single();
        var @event = type.GetMembers().OfType<IEventSymbol>().Single();
        var accessors = new IMethodSymbol[]
        {
            property.GetMethod!,
            @event.AddMethod!,
            @event.RemoveMethod!,
        };

        foreach (var accessor in accessors)
        {
            var formattedName = SymbolFormatter.GetFullyQualifiedName(accessor);
            var result = await SymbolResolver.ResolveAsync(
                solution,
                new SymbolSelector(formattedName, Project: "Demo"),
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, $"Could not resolve '{formattedName}': {result.Error}");
            Assert.Equal(accessor.MethodKind, Assert.IsAssignableFrom<IMethodSymbol>(
                result.Match!.Symbol).MethodKind);
        }
    }
}
