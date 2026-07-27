using DotSight.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Tests;

public sealed class SymbolImpactAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ClassifiesOnlyTheInvokedOrConstructedSymbol()
    {
        const string source =
            """
            using System;
            using System.Collections.Generic;

            public sealed class Box
            {
                public Box(int value) { }
            }

            public sealed class Probe
            {
                private int Field;
                private void Sink(int value) { }
                private void Generic<T>() { }

                public void Exercise()
                {
                    Sink(Field);
                    _ = new Box(Field);
                    _ = typeof(Box).GetMethod(nameof(Exercise));
                    _ = new Dictionary<Box, Probe>();
                    Generic<Box>();
                    _ = new int();
                    Action read = () => Console.WriteLine(Field);
                }
            }
            """;
        using var test = CreateSolution(source, withFilePath: true);
        var compilation = await test.Project.GetCompilationAsync(
            TestContext.Current.CancellationToken);
        var probe = compilation!.GetTypeByMetadataName("Probe")!;

        var fieldResult = await AnalyzeAsync(test, probe.GetMembers("Field").Single());
        Assert.All(fieldResult.References.Items, item => Assert.Equal("reference", item.Kind));
        var lambdaReference = Assert.Single(
            fieldResult.References.Items,
            item => item.Evidence.Snippet.Contains("Action read", StringComparison.Ordinal));
        Assert.Contains("Probe.Exercise", lambdaReference.Evidence.EnclosingSymbol, StringComparison.Ordinal);
        Assert.DoesNotContain("lambda expression", lambdaReference.Evidence.EnclosingSymbol, StringComparison.Ordinal);

        var sinkResult = await AnalyzeAsync(test, probe.GetMembers("Sink").Single());
        Assert.Contains(sinkResult.References.Items, item => item.Kind == "invocation");

        var box = compilation.GetTypeByMetadataName("Box")!;
        var boxResult = await AnalyzeAsync(test, box);
        Assert.Contains(
            boxResult.References.Items,
            item => item.Kind == "construction"
                && item.Evidence.Snippet.Contains("new Box", StringComparison.Ordinal));
        Assert.Contains(
            boxResult.References.Items,
            item => item.Kind == "type-use"
                && item.Evidence.Snippet.Contains("typeof(Box)", StringComparison.Ordinal));
        Assert.Contains(
            boxResult.References.Items,
            item => item.Kind == "type-use"
                && item.Evidence.Snippet.Contains("Dictionary<Box", StringComparison.Ordinal));
        Assert.Contains(
            boxResult.References.Items,
            item => item.Kind == "type-use"
                && item.Evidence.Snippet.Contains("Generic<Box>", StringComparison.Ordinal));

        var intResult = await AnalyzeAsync(
            test,
            compilation.GetSpecialType(SpecialType.System_Int32));
        Assert.Contains(
            intResult.References.Items,
            item => item.Kind == "construction"
                && item.Evidence.Snippet.Contains("new int()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotReportTruncationWhenEvidenceHasNoFilePath()
    {
        const string source =
            """
            public sealed class Probe
            {
                private int Field;
                public int Read() => Field;
            }
            """;
        using var test = CreateSolution(source, withFilePath: false);
        var compilation = await test.Project.GetCompilationAsync(
            TestContext.Current.CancellationToken);
        var field = compilation!
            .GetTypeByMetadataName("Probe")!
            .GetMembers("Field")
            .Single();

        var result = await AnalyzeAsync(test, field);

        Assert.Equal(1, result.References.Total);
        Assert.Equal(1, result.References.Returned);
        Assert.False(result.References.Truncated);
    }

    private static Task<SymbolImpactResult> AnalyzeAsync(
        ImpactTestSolution test,
        ISymbol symbol) =>
        SymbolImpactAnalyzer.AnalyzeAsync(
            test.Solution,
            new ResolvedSymbol(symbol, test.Project),
            depth: 1,
            maxResultsPerSection: 100,
            TestContext.Current.CancellationToken);

    private static ImpactTestSolution CreateSolution(string source, bool withFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Probe");
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Probe",
            "Probe",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(
                    Path.Combine(
                        Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                        "System.Runtime.dll")),
            ]);
        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Probe.cs",
                SourceText.From(source),
                filePath: withFilePath ? Path.Combine(Path.GetTempPath(), "Probe.cs") : null);
        Assert.True(workspace.TryApplyChanges(solution));
        return new ImpactTestSolution(workspace, workspace.CurrentSolution, projectId);
    }

    private sealed class ImpactTestSolution(
        AdhocWorkspace workspace,
        Solution solution,
        ProjectId projectId) : IDisposable
    {
        public Solution Solution { get; } = solution;

        public Project Project => Solution.GetProject(projectId)!;

        public void Dispose() => workspace.Dispose();
    }
}
