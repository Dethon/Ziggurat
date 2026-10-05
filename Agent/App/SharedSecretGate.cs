using Domain.Security;

namespace Agent.App;

// The gate on a group of the agent's own endpoints, so every route in it — one added later
// included — asks for the secret without asking to be gated. The comparison is SharedSecret's, the
// rule the MCP servers' /mcp is held to, and an unset secret refuses everything.
public static class SharedSecretGate
{
    public static RouteGroupBuilder RequireSharedSecret(this RouteGroupBuilder group, string sharedSecret)
    {
        group.AddEndpointFilter(async (context, next) =>
            SharedSecret.Matches(context.HttpContext.Request.Headers.Authorization.ToString(), sharedSecret)
                ? await next(context)
                : Results.Unauthorized());
        return group;
    }
}