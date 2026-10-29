using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Agent.App;

// The custom-agent registration endpoints: read a person's catalogue, add an agent to it, take one
// back. The public proxy routes /api/agents* here, so without a gate anyone on the internet could
// list a person's agents or register one that dials whatever endpoints it names. Its one caller,
// SexyTime's server, presents the shared secret on every verb; the gate is the group's, so a route
// added here later is gated without asking for it.
//
// The comparison is SharedSecret's, the same rule the outposts and the MCP servers are held to,
// and an unset secret refuses everything rather than leaving the door open.
//
// Whose catalogue it is comes from X-Ziggurat-User, which SexyTime's server copies off the
// signed-in request the proxy's session check handed it. The proxy does not check this route, so
// nothing strips a header a client wrote: it is believed only behind the secret, whose one holder
// is that server (.claude/rules/accounts.md). A userId in the query is never read.
public static class AgentApi
{
    public static void MapAgents(this WebApplication app, string sharedSecret)
    {
        var agents = app.MapGroup("/api/agents")
            .RequireSharedSecret(sharedSecret)
            .RequireForwardedUser();

        agents.MapGet("/", (IAgentDefinitionProvider provider, HttpContext context) =>
            provider.GetAll(UserOf(context)).Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)));

        agents.MapPost("/", (IAgentDefinitionProvider provider, HttpContext context, CustomAgentRegistration registration) =>
        {
            var definition = provider.RegisterCustomAgent(UserOf(context), registration);
            return new AgentCatalogEntry(definition.Id, definition.Name, definition.Description);
        });

        agents.MapDelete("/{agentId}", (IAgentDefinitionProvider provider, HttpContext context, string agentId) =>
            provider.UnregisterCustomAgent(UserOf(context), agentId));
    }

    // After the secret's gate, so a caller without the secret learns only that it lacks it. One
    // person or none: two values would join into an id nobody has.
    private static RouteGroupBuilder RequireForwardedUser(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers[ForwardedAccount.UserHeader] is [{ Length: > 0 }]
                ? await next(context)
                : Results.BadRequest($"{ForwardedAccount.UserHeader} must name one person."));
        return group;
    }

    private static string UserOf(HttpContext context) =>
        context.Request.Headers[ForwardedAccount.UserHeader].ToString();
}