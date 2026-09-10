using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt(ScheduleSetupSummary summary)
{
    [McpServerPrompt(Name = SchedulingPrompt.Name)]
    [Description(SchedulingPrompt.Description)]
    public string GetSchedulingPrompt()
    {
        var setup = summary.Get();
        var prompt = SchedulingPrompt.Prompt;
        return string.IsNullOrEmpty(setup)
            ? prompt
            : prompt + "\n\n" + setup;
    }
}