using System.Text.Json;
using DotSight.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotSight.Tests;

public sealed class WorkspaceServiceFreshnessTests
{
    [Fact]
    public async Task GetSolutionAsync_ReloadsAfterSavedSourceChanges()
    {
        using var directory = new TemporaryProject();
        var projectPath = directory.Write(
            "Freshness.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var sourcePath = directory.Write(
            "Feature.cs",
            "public sealed class Feature { public int Before => 1; }");
        using var workspace = new WorkspaceService(
            new WorkspaceOptions(projectPath),
            NullLogger<WorkspaceService>.Instance);

        var first = await workspace.GetSolutionAsync(ct: TestContext.Current.CancellationToken);
        var firstText = await first.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);
        var firstVersion = GetSnapshotVersion(workspace);

        directory.Write(
            "Feature.cs",
            "public sealed class Feature { public string After => \"updated\"; }");
        var second = await workspace.GetSolutionAsync(ct: TestContext.Current.CancellationToken);
        var secondText = await second.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Before", firstText.ToString(), StringComparison.Ordinal);
        Assert.Contains("After", secondText.ToString(), StringComparison.Ordinal);
        Assert.True(GetSnapshotVersion(workspace) > firstVersion);
    }

    private static long GetSnapshotVersion(WorkspaceService workspace)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(workspace.GetSnapshotInfo()));
        return json.RootElement.GetProperty("version").GetInt64();
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dotsight-workspace-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
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
