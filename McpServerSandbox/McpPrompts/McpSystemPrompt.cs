using System.ComponentModel;
using Domain.Prompts;
using Domain.Tools.Files;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt(SandboxFileSystem sandbox)
{
    private const string Name = "sandbox_prompt";

    // Built from the filesystem the server actually publishes, so the mount point and the workspace
    // the agent is told to work in are the ones this deployment has.
    [McpServerPrompt(Name = Name)]
    [Description("Explains the sandbox filesystem layout, capabilities, and limits")]
    public string GetSandboxPrompt() => SandboxPrompt.Build(sandbox.MountPoint, sandbox.Workspace);
}