using DotSight.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Tests;

internal sealed class TestSolution : IDisposable
{
    public required AdhocWorkspace Workspace { get; init; }
    public required Solution Solution { get; init; }
    public required ProjectId AppProjectId { get; init; }
    public required ProjectId TestProjectId { get; init; }
    public required string RootDirectory { get; init; }
    public required string AppSource { get; init; }
    public required string TestSource { get; init; }

    public void Dispose()
    {
        Workspace.Dispose();
        if (Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
    }
}

internal sealed class LinkedTestSolution : IDisposable
{
    public required AdhocWorkspace Workspace { get; init; }
    public required Solution Solution { get; init; }
    public required ProjectId CommonProjectId { get; init; }
    public required ProjectId ProjectAId { get; init; }
    public required ProjectId ProjectBId { get; init; }
    public required string RootDirectory { get; init; }

    public void Dispose()
    {
        Workspace.Dispose();
        if (Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
    }
}

internal static class TestSolutionFactory
{
    private static readonly MetadataReference[] SharedReferences =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    ];

    public static TestSolution CreateAgentWorkflowSolution(
        bool includeShiftedCompilerError = false)
    {
        const string baseAppSource =
            """
            namespace Demo;

            public interface IWorker
            {
                void Run(string value);
            }

            public sealed class Worker : IWorker
            {
                public string Name { get; set; } = "";

                public void Run(string value)
                {
                    Log(value);
                    void Deferred() { DeferredTarget(); }
                }

                public void Run(int value)
                {
                    Name = value.ToString();
                }

                private void Log(string value)
                {
                    System.Console.WriteLine(value);
                }

                private void DeferredTarget()
                {
                }
            }

            public sealed class Coordinator
            {
                public void Execute()
                {
                    var worker = new Worker();
                    worker.Run("job");
                }
            }
            """;
        var appSource = includeShiftedCompilerError
            ? baseAppSource.Replace(
                "    private void Log(string value)",
                "    public void Broken() { Run(\"value\"); MissingType missing; }\n\n"
                + "    private void Log(string value)",
                StringComparison.Ordinal)
            : baseAppSource;

        const string testSource =
            """
            namespace Demo.Tests;

            public sealed class WorkerTests
            {
                public void CallsRun()
                {
                    new Demo.Worker().Run("test");
                }
            }
            """;

        var root = Path.Combine(Path.GetTempPath(), "dotsight-tests", Guid.NewGuid().ToString("N"));
        var appFile = Path.Combine(root, "src", "Demo", "Workflow.cs");
        var testFile = Path.Combine(root, "tests", "Demo.Tests", "WorkerTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(appFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        File.WriteAllText(appFile, appSource);
        File.WriteAllText(testFile, testSource);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: Path.Combine(root, "Demo.sln")));

        var appProjectId = ProjectId.CreateNewId("Demo");
        var testProjectId = ProjectId.CreateNewId("Demo.Tests");
        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(
                appProjectId,
                "Demo",
                Path.Combine(root, "src", "Demo", "Demo.csproj")))
            .AddProject(CreateProjectInfo(
                testProjectId,
                "Demo.Tests",
                Path.Combine(root, "tests", "Demo.Tests", "Demo.Tests.csproj")))
            .AddProjectReference(testProjectId, new ProjectReference(appProjectId))
            .AddDocument(
                DocumentId.CreateNewId(appProjectId),
                "Workflow.cs",
                SourceText.From(appSource),
                filePath: appFile)
            .AddDocument(
                DocumentId.CreateNewId(testProjectId),
                "WorkerTests.cs",
                SourceText.From(testSource),
                filePath: testFile);

        Assert.True(workspace.TryApplyChanges(solution));
        return new TestSolution
        {
            Workspace = workspace,
            Solution = workspace.CurrentSolution,
            AppProjectId = appProjectId,
            TestProjectId = testProjectId,
            RootDirectory = root,
            AppSource = appSource,
            TestSource = testSource,
        };
    }

    public static async Task<ResolvedSymbol> ResolveStringRunMethodAsync(
        TestSolution testSolution,
        CancellationToken ct)
    {
        var project = testSolution.Solution.GetProject(testSolution.AppProjectId)!;
        var compilation = await project.GetCompilationAsync(ct);
        var worker = compilation!.GetTypeByMetadataName("Demo.Worker")!;
        var method = worker.GetMembers("Run")
            .OfType<IMethodSymbol>()
            .Single(candidate =>
                candidate.Parameters.Length == 1
                && candidate.Parameters[0].Type.SpecialType == SpecialType.System_String);
        return new ResolvedSymbol(method, project);
    }

    public static LinkedTestSolution CreateLinkedProjectSolution()
    {
        const string commonSource =
            """
            namespace Linked;

            public interface IWorker
            {
                void Run();
            }
            """;
        const string linkedSource =
            """
            namespace Linked;

            public sealed class Worker : IWorker
            {
                public void Run()
                {
                }

                public void Call(IWorker worker)
                {
                    worker.Run();
                }
            }
            """;

        var root = Path.Combine(
            Path.GetTempPath(),
            "dotsight-linked-tests",
            Guid.NewGuid().ToString("N"));
        var commonFile = Path.Combine(root, "Common", "IWorker.cs");
        var linkedFile = Path.Combine(root, "Shared", "Worker.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(commonFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linkedFile)!);
        File.WriteAllText(commonFile, commonSource);
        File.WriteAllText(linkedFile, linkedSource);

        var commonProjectId = ProjectId.CreateNewId("Common");
        var projectAId = ProjectId.CreateNewId("ProjectA");
        var projectBId = ProjectId.CreateNewId("ProjectB");
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: Path.Combine(root, "Linked.sln")));
        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(
                commonProjectId,
                "Common",
                Path.Combine(root, "Common", "Common.csproj")))
            .AddProject(CreateProjectInfo(
                projectAId,
                "ProjectA",
                Path.Combine(root, "ProjectA", "ProjectA.csproj")))
            .AddProject(CreateProjectInfo(
                projectBId,
                "ProjectB",
                Path.Combine(root, "ProjectB", "ProjectB.csproj")))
            .AddProjectReference(projectAId, new ProjectReference(commonProjectId))
            .AddProjectReference(projectBId, new ProjectReference(commonProjectId))
            .AddDocument(
                DocumentId.CreateNewId(commonProjectId),
                "IWorker.cs",
                SourceText.From(commonSource),
                filePath: commonFile)
            .AddDocument(
                DocumentId.CreateNewId(projectAId),
                "Worker.cs",
                SourceText.From(linkedSource),
                filePath: linkedFile)
            .AddDocument(
                DocumentId.CreateNewId(projectBId),
                "Worker.cs",
                SourceText.From(linkedSource),
                filePath: linkedFile);

        Assert.True(workspace.TryApplyChanges(solution));
        return new LinkedTestSolution
        {
            Workspace = workspace,
            Solution = workspace.CurrentSolution,
            CommonProjectId = commonProjectId,
            ProjectAId = projectAId,
            ProjectBId = projectBId,
            RootDirectory = root,
        };
    }

    private static ProjectInfo CreateProjectInfo(
        ProjectId id,
        string name,
        string filePath) =>
        ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: filePath,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: SharedReferences);
}
