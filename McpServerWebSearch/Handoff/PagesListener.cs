using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerWebSearch.Handoff;

// The pages people open answer on a listener of their own, and /mcp never does: the proxy routes
// /browse/* to that listener alone, so no routing mistake there can hand a browser this server's
// /mcp, and nothing on the MCP listener answers a page. Keyed on the socket the request arrived
// on, never the Host header, which the proxy passes on as the browser sent it.
//
// A startup filter placed first, so it stands ahead of the /mcp gate too: on the pages listener
// /mcp is not there at all, rather than there and asking for a credential.
internal sealed class PagesListener(ListenerConfiguration listeners) : IStartupFilter
{
    public const string Path = "/browse";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            var onPagesListener = context.Connection.LocalPort == listeners.Pages;
            if (onPagesListener != context.Request.Path.StartsWithSegments(Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await inner(context);
        });
        next(app);
    };
}

public static class PagesListenerExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPagesListener(ListenerConfiguration listeners)
        {
            services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new PagesListener(listeners)));
            return services;
        }
    }
}