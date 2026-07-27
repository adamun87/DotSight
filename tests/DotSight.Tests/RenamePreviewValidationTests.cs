using DotSight.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Tests;

public sealed class RenamePreviewValidationTests
{
    [Fact]
    public async Task PreviewAsync_ReportsFileRenameOnlyWhenRequested()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "dotsight-rename-file-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filePath = Path.Combine(root, "Widget.cs");
            const string source = "public sealed class Widget { }";
            File.WriteAllText(filePath, source);

            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId("Probe");
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "Probe",
                    "Probe",
                    LanguageNames.CSharp,
                    filePath: Path.Combine(root, "Probe.csproj"),
                    compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences:
                    [
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(
                            Path.Combine(
                                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                                "System.Runtime.dll")),
                    ]))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "Widget.cs",
                    SourceText.From(source),
                    filePath: filePath);
            Assert.True(workspace.TryApplyChanges(solution));
            solution = workspace.CurrentSolution;

            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync(
                TestContext.Current.CancellationToken);
            var target = compilation!.GetTypeByMetadataName("Widget")!;

            var withoutFileRename = await RenamePreviewService.PreviewAsync(
                solution,
                new ResolvedSymbol(target, project),
                "Gadget",
                new RenamePreviewOptions(false, false, false, false),
                maxEdits: 100,
                includeDiagnostics: false,
                TestContext.Current.CancellationToken);
            var withFileRename = await RenamePreviewService.PreviewAsync(
                solution,
                new ResolvedSymbol(target, project),
                "Gadget",
                new RenamePreviewOptions(false, false, false, true),
                maxEdits: 100,
                includeDiagnostics: false,
                TestContext.Current.CancellationToken);

            Assert.Null(Assert.Single(withoutFileRename.Files).NewFile);
            var renamedFile = Assert.Single(withFileRename.Files);
            Assert.Equal("Widget.cs", Path.GetFileName(renamedFile.File));
            Assert.Equal("Gadget.cs", Path.GetFileName(renamedFile.NewFile));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewAsync_ReportsConflictMovedToDifferentDeclarationAsNew()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "dotsight-rename-validation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filePath = Path.Combine(root, "Collision.cs");
            const string source =
                """
                public class Collision
                {
                    private int A;
                    private int A;
                    private int B;
                }
                """;
            File.WriteAllText(filePath, source);

            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId("Probe");
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "Probe",
                    "Probe",
                    LanguageNames.CSharp,
                    filePath: Path.Combine(root, "Probe.csproj"),
                    compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences:
                    [
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(
                            Path.Combine(
                                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                                "System.Runtime.dll")),
                    ]))
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "Collision.cs",
                    SourceText.From(source),
                    filePath: filePath);
            Assert.True(workspace.TryApplyChanges(solution));
            solution = workspace.CurrentSolution;

            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync(
                TestContext.Current.CancellationToken);
            var target = compilation!
                .GetTypeByMetadataName("Collision")!
                .GetMembers("A")
                .OfType<IFieldSymbol>()
                .First();

            var result = await RenamePreviewService.PreviewAsync(
                solution,
                new ResolvedSymbol(target, project),
                "B",
                new RenamePreviewOptions(false, false, false, false),
                maxEdits: 100,
                includeDiagnostics: true,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Validation.PreExistingCompilerErrors);
            Assert.Equal(1, result.Validation.NewCompilerErrors);
            var diagnostic = Assert.Single(result.Validation.Diagnostics);
            Assert.Equal("CS0102", diagnostic.Id);
            Assert.Contains("definition for 'B'", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
