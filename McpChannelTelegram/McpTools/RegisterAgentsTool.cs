using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelTelegram.McpTools;

// Telegram has no agent picker and no per-message model override, so this catalogue exists for one
// reason: the attachment capability resolution needs something to ask about the model a turn will
// run on. It arrives through the registration the agent already performs on connect and on every
// reconnect, so no new protocol call is added.
[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents reachable through Telegram (replaces any previously registered set)")]
    public string McpRun([Description("Agents available to Telegram")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        return $"registered {agents.Count} agents";
    }
}