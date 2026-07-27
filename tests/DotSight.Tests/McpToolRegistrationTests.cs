using System.Reflection;
using ModelContextProtocol.Server;

namespace DotSight.Tests;

/// <summary>
/// Validates that MCP tool registration attributes are correctly applied
/// following Microsoft MCP SDK conventions.
/// </summary>
public class McpToolRegistrationTests
{
    private static readonly Type[] ToolTypes =
    [
        typeof(DotSight.Tools.FindSymbolsTool),
        typeof(DotSight.Tools.GetSymbolDetailTool),
        typeof(DotSight.Tools.FindReferencesTool),
        typeof(DotSight.Tools.FindImplementationsTool),
        typeof(DotSight.Tools.GetDiagnosticsTool),
        typeof(DotSight.Tools.GetProjectGraphTool),
        typeof(DotSight.Tools.GetSourceTextTool),
        typeof(DotSight.Tools.GetDocumentSymbolsTool),
        typeof(DotSight.Tools.InspectPackageTool),
        typeof(DotSight.Tools.AnalyzeSymbolTool),
        typeof(DotSight.Tools.PreviewRenameTool),
    ];

    private static readonly string[] ExpectedToolNames =
    [
        "find_symbols",
        "get_symbol_detail",
        "find_references",
        "find_implementations",
        "get_diagnostics",
        "get_project_graph",
        "get_source_text",
        "get_document_symbols",
        "inspect_package",
        "analyze_symbol",
        "preview_rename",
    ];

    [Fact]
    public void AllToolTypes_HaveMcpServerToolTypeAttribute()
    {
        foreach (var type in ToolTypes)
        {
            var attr = type.GetCustomAttribute<McpServerToolTypeAttribute>();
            Assert.NotNull(attr);
        }
    }

    [Fact]
    public void AllToolMethods_HaveMcpServerToolAttribute()
    {
        foreach (var type in ToolTypes)
        {
            var toolMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .ToList();

            Assert.NotEmpty(toolMethods);
        }
    }

    [Fact]
    public void AllTools_HaveExpectedNames()
    {
        var actualNames = new List<string>();
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr?.Name is not null)
                    actualNames.Add(attr.Name);
            }
        }

        foreach (var expected in ExpectedToolNames)
        {
            Assert.Contains(expected, actualNames);
        }
    }

    [Fact]
    public void AllTools_AreReadOnly()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    Assert.True(attr.ReadOnly, $"Tool {attr.Name} should be ReadOnly");
                }
            }
        }
    }

    [Fact]
    public void AllTools_AreNotDestructive()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    Assert.False(attr.Destructive, $"Tool {attr.Name} should not be Destructive");
                }
            }
        }
    }

    [Fact]
    public void AllToolMethods_HaveDescriptionAttribute()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var descAttr = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                Assert.NotNull(descAttr);
                Assert.False(string.IsNullOrWhiteSpace(descAttr.Description),
                    $"Tool {toolAttr.Name} should have a non-empty description");
            }
        }
    }

    [Fact]
    public void AllToolMethods_AcceptCancellationToken()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var hasCt = method.GetParameters()
                    .Any(p => p.ParameterType == typeof(CancellationToken));
                Assert.True(hasCt, $"Tool {toolAttr.Name} should accept CancellationToken");
            }
        }
    }

    [Fact]
    public void AllToolMethods_ReturnTaskOfString()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                Assert.Equal(typeof(Task<string>), method.ReturnType);
            }
        }
    }

    [Fact]
    public void AllToolMethods_AcceptWorkspaceService()
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var hasWs = method.GetParameters()
                    .Any(p => p.ParameterType == typeof(DotSight.Services.WorkspaceService));
                Assert.True(hasWs, $"Tool {toolAttr.Name} should accept WorkspaceService via DI");
            }
        }
    }

    [Fact]
    public void ToolCount_MatchesExpected()
    {
        Assert.Equal(11, ToolTypes.Length);
        Assert.Equal(11, ExpectedToolNames.Length);
    }

    [Fact]
    public void PreviewRename_DefaultsCompilerDiagnosticsToOptIn()
    {
        var method = typeof(DotSight.Tools.PreviewRenameTool)
            .GetMethod(nameof(DotSight.Tools.PreviewRenameTool.PreviewRename))!;
        var parameter = method.GetParameters()
            .Single(candidate => candidate.Name == "includeDiagnostics");
        var description = parameter
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();

        Assert.Equal(false, parameter.DefaultValue);
        Assert.Contains("Default: false", description!.Description, StringComparison.Ordinal);
    }
}
