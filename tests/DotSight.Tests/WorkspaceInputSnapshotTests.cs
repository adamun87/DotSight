using DotSight.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Tests;

public sealed class WorkspaceInputSnapshotTests
{
    [Fact]
    public void EntryBaselineMatchesStableSolutionInputsWithoutBinaryReferences()
    {
        using var directory = new TemporaryDirectory();
        var projectPath = directory.Write(
            "Probe.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = directory.Write("Probe.cs", "public sealed class Probe { }");
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Probe");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Probe",
                "Probe",
                LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Probe.cs",
                SourceText.From("public sealed class Probe { }"),
                filePath: sourcePath);

        var baseline = WorkspaceInputSnapshot.Create(projectPath);
        var discovered = WorkspaceInputSnapshot.Create(solution, projectPath);

        Assert.True(baseline.HasSameReloadInputs(discovered));
    }

    [Fact]
    public void EntryBaselineIgnoresMissingPerProjectInputsWhenProjectsAreInSubdirectories()
    {
        using var directory = new TemporaryDirectory();
        var entryPath = directory.Write("Demo.sln", "");
        var projectPath = directory.Write(
            "src/Probe/Probe.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = directory.Write(
            "src/Probe/Probe.cs",
            "public sealed class Probe { }");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: entryPath));
        var projectId = ProjectId.CreateNewId("Probe");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Probe",
                "Probe",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Probe.cs",
                SourceText.From("public sealed class Probe { }"),
                filePath: sourcePath);

        var baseline = WorkspaceInputSnapshot.Create(entryPath);
        var discovered = WorkspaceInputSnapshot.Create(solution, entryPath);

        Assert.True(baseline.HasSameReloadInputs(discovered));
    }

    [Fact]
    public void EntryBaselineIgnoresExternalAnalyzerConfigurationForInitialRetry()
    {
        using var directory = new TemporaryDirectory();
        var entryPath = directory.Write("solution/Demo.sln", "");
        var projectPath = directory.Write(
            "solution/Probe.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = directory.Write(
            "solution/Probe.cs",
            "public sealed class Probe { }");
        var configPath = directory.Write(
            "sdk/analysis.globalconfig",
            "is_global = true");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: entryPath));
        var projectId = ProjectId.CreateNewId("Probe");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Probe",
                "Probe",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Probe.cs",
                SourceText.From("public sealed class Probe { }"),
                filePath: sourcePath)
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(projectId),
                "analysis.globalconfig",
                SourceText.From("is_global = true"),
                filePath: configPath);

        var baseline = WorkspaceInputSnapshot.Create(entryPath);
        var discovered = WorkspaceInputSnapshot.Create(solution, entryPath);

        Assert.True(baseline.HasSameReloadInputs(discovered));
    }

    [Fact]
    public void EntryBaselineRetriesForLinkedSourceOutsideInitialRoot()
    {
        using var directory = new TemporaryDirectory();
        var entryPath = directory.Write("solution/Demo.sln", "");
        var projectPath = directory.Write(
            "solution/Probe.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var linkedPath = directory.Write(
            "shared/Linked.cs",
            "public sealed class Linked { }");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: entryPath));
        var projectId = ProjectId.CreateNewId("Probe");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Probe",
                "Probe",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Linked.cs",
                SourceText.From("public sealed class Linked { }"),
                filePath: linkedPath);

        var baseline = WorkspaceInputSnapshot.Create(entryPath);
        var discovered = WorkspaceInputSnapshot.Create(solution, entryPath);

        Assert.False(baseline.HasSameReloadInputs(discovered));
    }

    [Fact]
    public void EntryBaselineDoesNotExpandIntoExternalPackageSource()
    {
        using var directory = new TemporaryDirectory();
        var entryPath = directory.Write("solution/Demo.sln", "");
        var projectPath = directory.Write(
            "solution/Probe.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var packageSourcePath = directory.Write(
            "packages/example/1.0.0/_content/PackageSource.cs",
            "public sealed class PackageSource { }");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: entryPath));
        var projectId = ProjectId.CreateNewId("Probe");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Probe",
                "Probe",
                LanguageNames.CSharp,
                filePath: projectPath))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "PackageSource.cs",
                SourceText.From("public sealed class PackageSource { }"),
                filePath: packageSourcePath);

        var baseline = WorkspaceInputSnapshot.Create(entryPath);
        var discovered = WorkspaceInputSnapshot.Create(solution, entryPath);

        Assert.True(baseline.HasSameReloadInputs(discovered));
    }

    [Fact]
    public void HasChanged_DetectsModifiedSavedSource()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("Feature.cs", "public class Feature { }");
        var snapshot = WorkspaceInputSnapshot.CreateForPaths([directory.Path], [source]);

        directory.Write("Feature.cs", "public class Feature { public int Value => 1; }");

        Assert.True(snapshot.HasChanged());
    }

    [Fact]
    public void HasChanged_DetectsAddedAndDeletedSource()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("Existing.cs", "public class Existing { }");
        var snapshot = WorkspaceInputSnapshot.CreateForPaths([directory.Path], [source]);

        directory.Write("Added.cs", "public class Added { }");

        Assert.True(snapshot.HasChanged());

        var snapshotAfterAdd = WorkspaceInputSnapshot.CreateForPaths([directory.Path], [source]);
        File.Delete(source);

        Assert.True(snapshotAfterAdd.HasChanged());
    }

    [Fact]
    public void HasChanged_DetectsCreatedBuildInput()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("Feature.cs", "public class Feature { }");
        var directoryBuildProps = System.IO.Path.Combine(directory.Path, "Directory.Build.props");
        var snapshot = WorkspaceInputSnapshot.CreateForPaths(
            [directory.Path],
            [source, directoryBuildProps]);

        directory.Write("Directory.Build.props", "<Project />");

        Assert.True(snapshot.HasChanged());
    }

    [Fact]
    public void HasChanged_DetectsModifiedCustomProjectImport()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("Feature.cs", "public class Feature { }");
        directory.Write("Build/Common.proj", "<Project />");
        var snapshot = WorkspaceInputSnapshot.CreateForPaths([directory.Path], [source]);

        directory.Write(
            "Build/Common.proj",
            "<Project><PropertyGroup><Feature>enabled</Feature></PropertyGroup></Project>");

        Assert.True(snapshot.HasChanged());
    }

    [Fact]
    public void HasChanged_ReturnsFalseWhenInputsAreUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("Feature.cs", "public class Feature { }");
        var snapshot = WorkspaceInputSnapshot.CreateForPaths([directory.Path], [source]);

        Assert.False(snapshot.HasChanged());
    }

    [Fact]
    public void HasChanged_DetectsModifiedLocalMetadataReference()
    {
        using var directory = new TemporaryDirectory();
        var entryPath = directory.Write("Demo.sln", "");
        var projectPath = directory.Write("Demo.csproj", "<Project />");
        var referencePath = System.IO.Path.Combine(directory.Path, "lib", "Dependency.dll");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(referencePath)!);
        File.Copy(typeof(object).Assembly.Location, referencePath);

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: entryPath));
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Demo",
            "Demo",
            LanguageNames.CSharp,
            filePath: projectPath,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(referencePath)]));
        Assert.True(workspace.TryApplyChanges(solution));
        var snapshot = WorkspaceInputSnapshot.Create(workspace.CurrentSolution, entryPath);

        File.SetLastWriteTimeUtc(referencePath, DateTime.UtcNow.AddMinutes(1));

        Assert.True(snapshot.HasChanged());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dotsight-input-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
