using Domain.Contracts;
using Domain.DTOs;
using Domain.Outposts;

namespace Agent.App;

// Three endpoints over the registry and nothing else: announce, keep alive, take back. The
// registry owns every decision — how long an entry lives, what is published when — so these stay
// one-liners, exactly as the custom-agent registration endpoint beside them is.
//
// The gate is OutpostSecret's, shared with the machine's own: anyone who can reach this port could
// otherwise attach a machine to somebody else's assistant.
public static class OutpostApi
{
    public static void MapOutposts(this WebApplication app, string sharedSecret)
    {
        var outposts = app.MapGroup("/api/outposts");
        outposts.AddEndpointFilter(async (context, next) =>
            OutpostSecret.Matches(
                context.HttpContext.Request.Headers.Authorization.ToString(), sharedSecret)
                ? await next(context)
                : Results.Unauthorized());

        // Refused rather than stored where the registration is one nobody could ever act on — a
        // blank name, an endpoint that is not an absolute URL. The shipped binary cannot produce
        // one, but this takes JSON from anything holding the secret.
        outposts.MapPost("/", async (IOutpostRegistry registry, OutpostRegistration registration, CancellationToken ct) =>
        {
            if (!registration.Registrable)
            {
                return Results.BadRequest(
                    "An outpost registration needs a non-empty name and an absolute endpoint URL.");
            }

            await registry.RegisterAsync(registration, ct);
            return Results.Ok();
        });

        // The answer carries the hub's verdict on this outpost's mount, which is the only channel
        // back to a machine: a shadowed outpost registered perfectly and simply is not there, and
        // nothing at the machine can detect that. A not-found means the registration had already
        // lapsed, which the machine reads as "announce yourself again" rather than as an error it
        // should back off from.
        outposts.MapPut("/{name}", async (IOutpostRegistry registry, string name, CancellationToken ct) =>
            await registry.KeepAliveAsync(name, ct) is { } verdict
                ? Results.Ok(new OutpostKeepAliveResponse(verdict))
                : Results.NotFound());

        outposts.MapDelete("/{name}", async (IOutpostRegistry registry, string name, CancellationToken ct) =>
            await registry.DeregisterAsync(name, ct) ? Results.Ok() : Results.NotFound());
    }
}

// One verdict, and deliberately nothing else. The keepalive stays a liveness ping: an outpost
// reports no telemetry of its own, and this is not the place to start.
public sealed record OutpostKeepAliveResponse(OutpostVerdict Verdict);