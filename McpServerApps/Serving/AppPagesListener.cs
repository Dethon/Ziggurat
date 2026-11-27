using McpServerApps.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerApps.Serving;

// App origins answer on a listener of their own, and /mcp never does: the proxy sends every
// `*.<apps domain>` request to that listener alone, so no Host a browser sends can reach this
// server's /mcp, and nothing on the MCP listener serves an app. Keyed on the socket the request
// arrived on, never the Host header, which the proxy passes on as the browser sent it. A startup
// filter placed first, ahead of the /mcp gate, so on the pages listener /mcp is not there at all.
internal sealed class AppPagesListener(AppListeners listeners) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            if (context.Connection.LocalPort == listeners.Pages)
            {
                await context.RequestServices.GetRequiredService<AppPages>().ServeAsync(context);
                return;
            }

            await inner(context);
        });
        next(app);
    };
}

public static class AppPagesListenerExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAppPages(AppListeners listeners)
        {
            services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new AppPagesListener(listeners)));
            return services.AddSingleton<AppPages>();
        }
    }
}