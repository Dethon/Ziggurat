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
// Whose catalogue it is comes from X-Ziggurat-Assertion, which SexyTime's server copies off the
// signed-in request the proxy's session check handed it: the accounts service's signature over the
// person, for SexyTime's channel, verified here against the service's published keys. The secret
// says the caller is that server; the assertion says who it is acting for, so a holder of the
// secret can no longer name anyone it likes. Neither opens the API alone. The plain X-Ziggurat-User
// beside it, and a userId in the query, are never read (.claude/rules/accounts.md).
public static class AgentApi
{
    public static void MapAgents(this WebApplication app, string sharedSecret)
    {
        var agents = app.MapGroup("/api/agents")
            .RequireSharedSecret(sharedSecret)
            .RequireAssertedPerson();

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

    private static readonly object _personItem = new();

    // After the secret's gate, so a caller without the secret learns only that it lacks it. Without
    // a believable assertion the call is signed out, as a request past the session check without one
    // is everywhere else. One header value or none: the session check answers one.
    private static RouteGroupBuilder RequireAssertedPerson(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var person = await http.RequestServices.GetRequiredService<AssertionVerifier>()
                .VerifyRequestAsync(http.Request.Headers, AppChannels.SexyTime, http.RequestAborted);
            if (person is null)
            {
                return Results.Unauthorized();
            }

            http.Items[_personItem] = person;
            return await next(context);
        });
        return group;
    }

    private static string UserOf(HttpContext context) => ((ForwardedAccount)context.Items[_personItem]!).UserId;
}