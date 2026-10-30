using Domain.Security.Signing;

namespace Agent.App;

// The public half of the agent's signing keys, for every server's gate to check a call token with.
// Asked without a credential — public keys are no secret, and the servers that fetch them hold no
// credential for the agent — and reached on the deployment's own network alone: the proxy routes
// /api/agents* and /api/outposts* here and nothing else, so this path lands on another upstream on
// every host (DeploymentConformanceTests).
public static class AgentKeysApi
{
    public const string Path = "/internal/jwks";

    public static void MapAgentKeys(this IEndpointRouteBuilder app) =>
        app.MapGet(Path, (HttpContext http, SigningKeyStore keys) =>
        {
            // Never cached on the way: a rotated key is fetched by id the moment a token names it.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(keys.PublishedKeys.ToJson(), "application/json");
        });
}