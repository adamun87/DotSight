using System.Collections.Concurrent;
using System.Text.Json;
using DotSight.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotSight.Tests;

public sealed class WorkspaceServiceFreshnessTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReloadsAfterSavedSourceChanges()
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

        using var first = await workspace.GetSnapshotAsync(ct: TestContext.Current.CancellationToken);
        var firstText = await first.Solution.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);
        var firstVersion = GetSnapshotVersion(workspace);

        directory.Write(
            "Feature.cs",
            "public sealed class Feature { public string After => \"updated\"; }");
        using var second = await workspace.GetSnapshotAsync(ct: TestContext.Current.CancellationToken);
        var secondText = await second.Solution.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Before", firstText.ToString(), StringComparison.Ordinal);
        Assert.Contains("After", secondText.ToString(), StringComparison.Ordinal);
        Assert.True(GetSnapshotVersion(workspace) > firstVersion);
    }

    [Fact]
    public async Task GetSnapshotAsync_OwnsSourceAndInfoAcrossReload()
    {
        using var directory = new TemporaryProject();
        var projectPath = directory.Write(
            "Owned.csproj",
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

        using var first = await workspace.GetSnapshotAsync(
            ct: TestContext.Current.CancellationToken);
        directory.Write(
            "Feature.cs",
            "public sealed class Feature { public string After => \"updated\"; }");
        using var second = await workspace.GetSnapshotAsync(
            ct: TestContext.Current.CancellationToken);

        var firstText = await first.Solution.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);
        var secondText = await second.Solution.Projects.Single().Documents
            .Single(document => document.FilePath == sourcePath)
            .GetTextAsync(TestContext.Current.CancellationToken);
        var firstCompilation = await first.Solution.Projects.Single()
            .GetCompilationAsync(TestContext.Current.CancellationToken);
        var secondCompilation = await second.Solution.Projects.Single()
            .GetCompilationAsync(TestContext.Current.CancellationToken);
        var firstFeature = firstCompilation!.GetTypeByMetadataName("Feature")!;
        var secondFeature = secondCompilation!.GetTypeByMetadataName("Feature")!;

        Assert.Contains("Before", firstText.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("After", firstText.ToString(), StringComparison.Ordinal);
        Assert.Contains("After", secondText.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(firstFeature.GetMembers("Before"));
        Assert.Empty(firstFeature.GetMembers("After"));
        Assert.NotEmpty(secondFeature.GetMembers("After"));
        Assert.True(second.Info.Version > first.Info.Version);
    }

    [Fact]
    public async Task GetSnapshotAsync_OpensSolutionOnceWhenProjectDeclaresAdditionalFile()
    {
        using var directory = new TemporaryProject();
        var solutionPath = directory.Write(
            "AdditionalInputs.slnx",
            """
            <Solution>
              <Project Path="AdditionalInputs.csproj" />
            </Solution>
            """);
        directory.Write(
            "AdditionalInputs.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <AdditionalFiles Include="settings.json" />
              </ItemGroup>
            </Project>
            """);
        directory.Write(
            "Feature.cs",
            "public sealed class Feature { }");
        directory.Write("settings.json", "{}");
        var logger = new OpeningAttemptLogger();
        using var workspace = new WorkspaceService(
            new WorkspaceOptions(solutionPath),
            logger);

        using var snapshot = await workspace.GetSnapshotAsync(
            ct: TestContext.Current.CancellationToken);

        var project = Assert.Single(snapshot.Solution.Projects);
        Assert.Equal("settings.json", Assert.Single(project.AdditionalDocuments).Name);
        Assert.True(
            logger.OpeningAttempts == 1,
            string.Join(Environment.NewLine, logger.Messages));
    }

    private static long GetSnapshotVersion(WorkspaceService workspace)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(workspace.GetSnapshotInfo()));
        return json.RootElement.GetProperty("version").GetInt64();
    }

    private sealed class OpeningAttemptLogger : ILogger<WorkspaceService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public int OpeningAttempts => Messages.Count(
            message => message.StartsWith("Opening:", StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
        }
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
