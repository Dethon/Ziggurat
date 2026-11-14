using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = SchedulingPrompt.Name)]
    [Description(SchedulingPrompt.Description)]
    public string GetSchedulingPrompt() => SchedulingPrompt.Prompt;
}