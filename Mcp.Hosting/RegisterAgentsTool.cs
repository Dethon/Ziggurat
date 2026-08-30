using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// What accepting the agent's catalog registration means for a server whose only use of it is
// holding the current set: replace and count. Registered explicitly by the servers that want it
// (Telegram, voice, scheduling) — a server that must do more on registration (SignalR's hub
// broadcast) or deliberately nothing (the library) keeps a tool of its own.
[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agent catalog (replaces any previously registered set)")]
    public string McpRun([Description("Registered agents")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        return $"registered {agents.Count} agents";
    }
}