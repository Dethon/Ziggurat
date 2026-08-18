using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = WebBrowsingPrompt.Name)]
    [Description(WebBrowsingPrompt.Description)]
    public static string GetSystemPrompt()
    {
        return WebBrowsingPrompt.AgentSystemPrompt;
    }
}