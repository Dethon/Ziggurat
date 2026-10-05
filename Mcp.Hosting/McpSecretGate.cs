using Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Mcp.Hosting;

// The gate on every server's /mcp, installed by AddMcpHost so that being hosted is being gated.
// A tool reads the caller's conversation context off a call's _meta and believes it, so without
// this anyone who could reach a container's port could act as any conversation.
//
// A startup filter, because a server maps its own endpoints in Program.cs and the host only ever
// sees its services: a filter is the one thing a service registration can add to the pipeline,
// and it runs ahead of everything the server adds itself. Ahead of routing, too, which is why it
// guards a path rather than an endpoint — and why that path alone: a server's other endpoints are
// called by browsers, Home Assistant and satellites, which hold no deployment secret, and the ones
// that need a gate carry their own token. McpServerTableTests holds every server to mapping its
// MCP endpoint here.
internal sealed class McpSecretGate(string secret) : IStartupFilter
{
    public const string Path = "/mcp";

    public string Secret { get; } = secret;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(Path),
            gated => gated.Use(async (context, inner) =>
            {
                if (!SharedSecret.Matches(context.Request.Headers.Authorization.ToString(), Secret))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await inner(context);
            }));
        next(app);
    };
}