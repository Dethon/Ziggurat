using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Agent.App;

// The custom-agent registration endpoints: read a user's catalogue, add an agent to it, take one
// back. The public proxy routes /api/agents* here, so without a gate anyone on the internet could
// list a person's agents or register one that dials whatever endpoints it names. Its one caller,
// SexyTime, presents the shared secret on every verb; the gate is the group's, so a route added
// here later is gated without asking for it.
//
// The comparison is SharedSecret's, the same rule the outposts and the MCP servers are held to,
// and an unset secret refuses everything rather than leaving the door open.
public static class AgentApi
{
    public static void MapAgents(this WebApplication app, string sharedSecret)
    {
        var agents = app.MapGroup("/api/agents").RequireSharedSecret(sharedSecret);

        agents.MapGet("/", (IAgentDefinitionProvider provider, string? userId) =>
            provider.GetAll(userId).Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)));

        agents.MapPost("/", (IAgentDefinitionProvider provider, string userId, CustomAgentRegistration registration) =>
        {
            var definition = provider.RegisterCustomAgent(userId, registration);
            return new AgentCatalogEntry(definition.Id, definition.Name, definition.Description);
        });

        agents.MapDelete("/{agentId}", (IAgentDefinitionProvider provider, string userId, string agentId) =>
            provider.UnregisterCustomAgent(userId, agentId));
    }
}