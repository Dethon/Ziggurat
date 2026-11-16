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

    // The same workings for an agent that is not the download assistant: the agent's manifest gives
    // each its audience, so either agent reads one of the two.
    [McpServerPrompt(Name = LibraryGuide.Name)]
    [Description(LibraryGuide.Description)]
    public static string GetLibraryGuide() => LibraryGuide.Text;
}