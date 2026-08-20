using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = DownloaderPrompt.Name)]
    [Description(DownloaderPrompt.Description)]
    public static string GetSystemPrompt()
    {
        return DownloaderPrompt.AgentSystemPrompt;
    }
}