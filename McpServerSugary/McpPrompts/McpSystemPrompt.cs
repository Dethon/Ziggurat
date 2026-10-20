using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerSugary.McpPrompts;

// The words the sugary agent estimates by. They are this server's, as the schema the reply is read
// against is, so the two change together.
[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = SugaryPrompt.Name)]
    [Description(SugaryPrompt.Description)]
    public static string GetSugaryPrompt() => SugaryPrompt.Prompt;
}