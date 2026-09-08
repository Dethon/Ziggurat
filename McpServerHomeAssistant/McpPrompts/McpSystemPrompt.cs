using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpPrompts;

// The static guide alone. The setup index used to be appended here, as old as the conversation
// that fetched it; it is now a file the mount builds on every read (docs/adr/0039).
[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = HomeAssistantPrompt.Name)]
    [Description(HomeAssistantPrompt.Description)]
    public static string GetSystemPrompt() => HomeAssistantPrompt.SystemPrompt;
}