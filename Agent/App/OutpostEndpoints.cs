using System.Security.Cryptography;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;

namespace Agent.App;

// The one gate on registration: anyone who can reach this port could otherwise attach a machine to
// somebody else's assistant. Both directions present the same shared secret — the outpost when it
// registers and keeps alive, the agent when it dials the machine back.
public static class OutpostSecret
{
    private const string Scheme = "Bearer ";

    // An unset secret refuses everything. The alternative reading — no secret configured meaning no
    // gate — turns a forgotten environment variable into an open door onto whatever filesystems
    // happen to be on the network.
    public static bool Matches(string? presented, string configured)
    {
        if (string.IsNullOrEmpty(configured)
            || presented is null
            || !presented.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented[Scheme.Length..]),
            Encoding.UTF8.GetBytes(configured));
    }
}

// Three endpoints over the registry and nothing else: announce, keep alive, take back. The
// registry owns every decision — how long an entry lives, what is published when — so these stay
// one-liners, exactly as the custom-agent registration endpoint beside them is.
public static class OutpostEndpoints
{
    public static void MapOutposts(this WebApplication app, string sharedSecret)
    {
        var outposts = app.MapGroup("/api/outposts");
        outposts.AddEndpointFilter(async (context, next) =>
            OutpostSecret.Matches(
                context.HttpContext.Request.Headers.Authorization.ToString(), sharedSecret)
                ? await next(context)
                : Results.Unauthorized());

        outposts.MapPost("/", (IOutpostRegistry registry, OutpostRegistration registration, CancellationToken ct) =>
            registry.RegisterAsync(registration, ct));

        // False means the registration had already lapsed, which the machine reads as "announce
        // yourself again" rather than as an error it should back off from.
        outposts.MapPut("/{name}", async (IOutpostRegistry registry, string name, CancellationToken ct) =>
            await registry.KeepAliveAsync(name, ct) ? Results.Ok() : Results.NotFound());

        outposts.MapDelete("/{name}", async (IOutpostRegistry registry, string name, CancellationToken ct) =>
            await registry.DeregisterAsync(name, ct) ? Results.Ok() : Results.NotFound());
    }
}