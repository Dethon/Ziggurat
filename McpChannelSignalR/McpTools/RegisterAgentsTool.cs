using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog, IHubNotificationSender hubSender)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents available to WebChat (replaces any previously registered set)")]
    public string McpRun(
        [Description("Agents available to WebChat")] IReadOnlyList<AgentCatalogEntry> agents,
        [Description("Each household's own Lemonade box's models, by household")]
        IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>>? householdLemonadeModels = null)
    {
        catalog.Replace(agents, householdLemonadeModels);
        // Best-effort UI refresh; a client-push failure must not block registration. These are the
        // home's agents, so a browser is told to ask for its own household's, never sent them.
        _ = hubSender.SendAsync("OnAgentsUpdated", new AgentsUpdatedNotification());
        return $"registered {agents.Count} agents";
    }
}