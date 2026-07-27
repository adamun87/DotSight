using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DotSight.Services;

// MSBuild locator must be registered before any Roslyn/MSBuild types are loaded
MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

// MCP servers use stdio — redirect all logging to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Parse --solution argument (lazy: if not specified, discovered on first tool call)
var solutionPath = GetExplicitSolutionPath(args);
builder.Services.AddSingleton(new WorkspaceOptions(solutionPath));
builder.Services.AddSingleton<WorkspaceService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "DotSight",
            Version = "0.1.0-preview.7"
        };
        options.ServerInstructions = """
            C# solution intelligence for coding agents. Tools analyze saved files and refresh their Roslyn workspace when source or relevant MSBuild inputs change.

            Choose the narrowest tool for the task:
            - analyze_symbol — preferred for impact analysis and bounded call flow: references, callers, outgoing calls/constructions, implementations, overrides, snippets, and test-project evidence
            - preview_rename — compute Roslyn rename edits and optional post-rename compiler checks without modifying files
            - find_symbols — locate a symbol, then use its exact signature or source position when overloads are ambiguous
            - get_symbol_detail / get_source_text / get_document_symbols — inspect targeted declarations and implementations
            - find_references / find_implementations — focused single-section navigation
            - get_project_graph — project/dependency overview; request outlines only when a broad architecture map is actually needed
            - get_diagnostics — compiler errors, warnings, and analyzer issues
            - inspect_package — inspect a NuGet package API

            Semantic tools are static analysis. Continue to use builds, tests, and runtime evidence for validation.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static string? GetExplicitSolutionPath(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--solution" or "-s")
            return Path.GetFullPath(args[i + 1]);
    }
    return null;
}
