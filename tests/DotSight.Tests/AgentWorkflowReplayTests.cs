using System.Security.Cryptography;
using System.Text;
using DotSight.Services;

namespace DotSight.Tests;

public sealed class AgentWorkflowReplayTests
{
    [Fact]
    public async Task AnalyzeSymbol_ReplaysImpactAndCallFlowWorkflow()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await SymbolImpactAnalyzer.AnalyzeAsync(
            testSolution.Solution,
            target,
            depth: 1,
            maxResultsPerSection: 100,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            result.IncomingCalls.Items,
            call => call.From.FullyQualifiedName.Contains(
                "Demo.Coordinator.Execute",
                StringComparison.Ordinal));
        Assert.Contains(
            result.IncomingCalls.Items,
            call => call.From.Project == "Demo.Tests"
                && call.Evidence.IsTestProject
                && call.Evidence.TestProjectEvidence == "project name");
        Assert.Contains(
            result.OutgoingCalls.Items,
            call => call.To.FullyQualifiedName.Contains(
                "Demo.Worker.Log",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.OutgoingCalls.Items,
            call => call.To.FullyQualifiedName.Contains(
                "Demo.Worker.DeferredTarget",
                StringComparison.Ordinal));
        Assert.True(result.TestReferenceCount > 0);
        Assert.All(
            result.References.Items,
            reference => Assert.False(string.IsNullOrWhiteSpace(reference.Evidence.Snippet)));
    }

    [Fact]
    public async Task AnalyzeSymbol_BoundsMaterializedReferencesButReportsExactTotal()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await SymbolImpactAnalyzer.AnalyzeAsync(
            testSolution.Solution,
            target,
            depth: 1,
            maxResultsPerSection: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.References.Returned);
        Assert.True(result.References.Total > result.References.Returned);
        Assert.True(result.References.Truncated);
        Assert.True(result.TestReferenceCount > 0);
    }

    [Fact]
    public async Task AnalyzeSymbol_PreservesLinkedFileEvidencePerProject()
    {
        using var testSolution = TestSolutionFactory.CreateLinkedProjectSolution();
        var commonProject = testSolution.Solution.GetProject(testSolution.CommonProjectId)!;
        var compilation = await commonProject.GetCompilationAsync(
            TestContext.Current.CancellationToken);
        var target = compilation!
            .GetTypeByMetadataName("Linked.IWorker")!
            .GetMembers("Run")
            .Single();

        var result = await SymbolImpactAnalyzer.AnalyzeAsync(
            testSolution.Solution,
            new ResolvedSymbol(target, commonProject),
            depth: 1,
            maxResultsPerSection: 100,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.References.Items, item => item.Evidence.Project == "ProjectA");
        Assert.Contains(result.References.Items, item => item.Evidence.Project == "ProjectB");
        Assert.Contains(result.Implementations.Items, item => item.Project == "ProjectA");
        Assert.Contains(result.Implementations.Items, item => item.Project == "ProjectB");
    }

    [Fact]
    public async Task AnalyzeSymbol_FindsInterfaceImplementation()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var project = testSolution.Solution.GetProject(testSolution.AppProjectId)!;
        var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);
        var interfaceMethod = compilation!
            .GetTypeByMetadataName("Demo.IWorker")!
            .GetMembers("Run")
            .Single();

        var result = await SymbolImpactAnalyzer.AnalyzeAsync(
            testSolution.Solution,
            new ResolvedSymbol(interfaceMethod, project),
            depth: 1,
            maxResultsPerSection: 100,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            result.Implementations.Items,
            implementation => implementation.FullyQualifiedName.Contains(
                "Demo.Worker.Run",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewRename_ReplaysCrossProjectRenameWithoutChangingOriginalSolution()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            target,
            "Process",
            new RenamePreviewOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false),
            maxEdits: 100,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Truncated);
        Assert.True(result.ChangedFiles >= 2);
        Assert.Contains(result.Files, file => file.File?.EndsWith("Workflow.cs") == true);
        Assert.Contains(result.Files, file => file.File?.EndsWith("WorkerTests.cs") == true);
        Assert.All(result.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.BaseSha256)));
        Assert.Contains(
            result.Files.SelectMany(file => file.Edits),
            edit => edit.OldText == "Run" && edit.NewText == "Process");
        Assert.Equal(0, result.Validation.NewCompilerErrors);

        var originalTexts = await Task.WhenAll(
            testSolution.Solution.Projects
                .SelectMany(project => project.Documents)
                .Select(document => document.GetTextAsync(TestContext.Current.CancellationToken)));
        Assert.Contains(originalTexts, text => text.ToString().Contains("Run(\"job\")", StringComparison.Ordinal));
        Assert.DoesNotContain(originalTexts, text => text.ToString().Contains("Process(\"job\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewRename_DoesNotRenameSiblingOverloadUnlessRequested()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);
        var intOverloadLine = testSolution.AppSource
            .Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .Single(item => item.line.Contains("Run(int value)", StringComparison.Ordinal))
            .number;

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            target,
            "Process",
            new RenamePreviewOptions(false, false, false, false),
            maxEdits: 100,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            result.Files.SelectMany(file => file.Edits),
            edit => edit.StartLine == intOverloadLine);
    }

    [Fact]
    public async Task PreviewRename_HashesExactSavedFileBytes()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var appDocument = testSolution.Solution.GetProject(testSolution.AppProjectId)!
            .Documents
            .Single();
        File.WriteAllText(appDocument.FilePath!, testSolution.AppSource, Encoding.Unicode);
        var expectedHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(
                    appDocument.FilePath!,
                    TestContext.Current.CancellationToken)))
            .ToLowerInvariant();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            target,
            "Process",
            new RenamePreviewOptions(false, false, false, false),
            maxEdits: 100,
            includeDiagnostics: false,
            TestContext.Current.CancellationToken);

        var appPreview = Assert.Single(
            result.Files,
            file => file.File?.EndsWith("Workflow.cs", StringComparison.Ordinal) == true);
        Assert.Equal(expectedHash, appPreview.BaseSha256);
    }

    [Fact]
    public async Task PreviewRename_OmitsFilesWithoutReturnedEditsWhenTruncated()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            target,
            "Process",
            new RenamePreviewOptions(false, false, false, false),
            maxEdits: 1,
            includeDiagnostics: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Truncated);
        Assert.Equal(1, result.ReturnedEdits);
        Assert.True(result.ChangedFiles > result.Files.Count);
        Assert.All(result.Files, file => Assert.NotEmpty(file.Edits));
    }

    [Fact]
    public async Task PreviewRename_DoesNotTreatShiftedPreExistingErrorAsNew()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution(
            includeShiftedCompilerError: true);
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            target,
            "ProcessWithLongerName",
            new RenamePreviewOptions(false, false, false, false),
            maxEdits: 100,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Validation.PreExistingCompilerErrors > 0);
        Assert.Equal(0, result.Validation.NewCompilerErrors);
    }

    [Fact]
    public async Task PreviewRename_RejectsDeletedSavedFile()
    {
        using var testSolution = TestSolutionFactory.CreateAgentWorkflowSolution();
        var target = await TestSolutionFactory.ResolveStringRunMethodAsync(
            testSolution,
            TestContext.Current.CancellationToken);
        var appDocument = testSolution.Solution.GetProject(testSolution.AppProjectId)!
            .Documents
            .Single();
        File.Delete(appDocument.FilePath!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RenamePreviewService.PreviewAsync(
                testSolution.Solution,
                target,
                "Process",
                new RenamePreviewOptions(false, false, false, false),
                maxEdits: 100,
                includeDiagnostics: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("was deleted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewRename_DeduplicatesLinkedFileEdits()
    {
        using var testSolution = TestSolutionFactory.CreateLinkedProjectSolution();
        var projectA = testSolution.Solution.GetProject(testSolution.ProjectAId)!;
        var compilation = await projectA.GetCompilationAsync(TestContext.Current.CancellationToken);
        var target = compilation!
            .GetTypeByMetadataName("Linked.Worker")!
            .GetMembers("Run")
            .Single();

        var result = await RenamePreviewService.PreviewAsync(
            testSolution.Solution,
            new ResolvedSymbol(target, projectA),
            "Process",
            new RenamePreviewOptions(false, false, false, false),
            maxEdits: 100,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        var linkedFile = Assert.Single(
            result.Files,
            file => string.Equals(
                Path.GetFileName(file.File),
                "Worker.cs",
                StringComparison.Ordinal));
        Assert.Equal(
            linkedFile.Edits.Count,
            linkedFile.Edits
                .Select(edit => (edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn))
                .Distinct()
                .Count());
        Assert.Equal(
            result.Files.Sum(file => file.TotalEdits),
            result.TotalEdits);
    }
}
