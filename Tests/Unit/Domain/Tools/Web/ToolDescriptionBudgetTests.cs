using System.ComponentModel;
using System.Reflection;
using Domain.Prompts;
using Domain.Tools.FileSystem;
using McpServerWebSearch.McpTools;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// A tool's description is re-sent on every request of every conversation that has the tool,
// like a standing prompt section — and unlike one, it had no budget, so the web tools grew a
// workflow, an essay on one flag and the snippet rule in it, all of which the web-browsing
// skill carries and a web turn loads by rule. The budgets below are what a description is for:
// what the tool does and what comes back. What the model must know before it calls is the
// skill's, and the parameters describe themselves.
public class ToolDescriptionBudgetTests
{
    public static TheoryData<string, int> Budgets => new()
    {
        { WebSearchToolName(typeof(McpWebSearchTool)), 60 },
        { WebSearchToolName(typeof(McpWebBrowseTool)), 90 },
        { WebSearchToolName(typeof(McpWebSnapshotTool)), 40 },
        { WebSearchToolName(typeof(McpWebActionTool)), 90 },
        { WebSearchToolName(typeof(McpViewImageTool)), 60 },
    };

    [Theory]
    [MemberData(nameof(Budgets))]
    public void EachWebToolDescription_FitsItsBudget(string tool, int budget)
    {
        var description = _descriptions[tool];

        PromptTokens.Estimate(description).ShouldBeLessThanOrEqualTo(
            budget,
            $"{tool}'s description is {PromptTokens.Estimate(description)} tokens against {budget}; " +
            "what the model must know before it calls belongs in the web-browsing skill");
    }

    [Fact]
    public void ExecDescription_FitsItsBudget()
    {
        PromptTokens.Estimate(VfsExecTool.ToolDescription).ShouldBeLessThanOrEqualTo(
            120,
            "exec's description carries a mount's path spelling, which is the sandbox skill's");
    }

    private static readonly Dictionary<string, string> _descriptions = new[]
        {
            typeof(McpWebSearchTool), typeof(McpWebBrowseTool), typeof(McpWebSnapshotTool),
            typeof(McpWebActionTool), typeof(McpViewImageTool)
        }
        .Select(t => t.GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        .ToDictionary(
            m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
            m => m.GetCustomAttribute<DescriptionAttribute>()!.Description);

    private static string WebSearchToolName(Type tool) =>
        tool.GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .GetCustomAttribute<McpServerToolAttribute>()!.Name!;
}