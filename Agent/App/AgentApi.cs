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
// The comparison is SharedSecret's, the same rule the MCP servers are held to,
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

        // The person's household's agents, then their own.
        agents.MapGet("/", async (IAgentDefinitionProvider provider, HttpContext context) =>
            (await provider.GetAllAsync(AgentApiOwner.Of(context).TenantId, AgentApiOwner.Of(context), context.RequestAborted))
            .Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)));

        agents.MapPost("/", async (IAgentDefinitionProvider provider, HttpContext context, CustomAgentRegistration registration) =>
        {
            var definition = await provider.RegisterCustomAgentAsync(AgentApiOwner.Of(context), registration, context.RequestAborted);
            return new AgentCatalogEntry(definition.Id, definition.Name, definition.Description);
        });

        agents.MapDelete("/{agentId}", (IAgentDefinitionProvider provider, HttpContext context, string agentId) =>
            provider.UnregisterCustomAgentAsync(AgentApiOwner.Of(context), agentId, context.RequestAborted));
    }

    // After the secret's gate, so a caller without the secret learns only that it lacks it. Without
    // a believable assertion the call is signed out, as a request past the session check without one
    // is everywhere else. One header value or none: the session check answers one.
    private static RouteGroupBuilder RequireAssertedPerson(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (await http.RequestServices.GetRequiredService<AssertionVerifier>()
                    .AdmitAsync(http.Request.Headers, AppChannels.SexyTime, null, http.RequestAborted)
                is not AppAdmission.Admitted { Person: var person })
            {
                return Results.Unauthorized();
            }

            AgentApiOwner.Keep(http, person);
            return await next(context);
        });
        return group;
    }
}

// Whose catalogue a request is about: the household and the person both, as the assertion the gate
// verified signed them — a custom agent is kept under the pair, so the same person id in another
// household names a catalogue of its own. Read by the routes, which run only behind the gate (an
// endpoint filter, so after parameter binding: a binder would see nobody yet).
internal static class AgentApiOwner
{
    private static readonly object _item = new();

    public static Account Of(HttpContext http) =>
        http.Items.TryGetValue(_item, out var owner) && owner is Account account
            ? account
            : throw new InvalidOperationException("An /api/agents route ran with no owner: it is not behind the gate");

    public static void Keep(HttpContext http, ForwardedAccount person) => http.Items[_item] = person.Account;
}