using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The two calls that sit beside AddChannelServer, so a reader comparing them sees exactly what a
// channel adds.
//
// AddMcpHost is the four things every MCP server in the repo has, whatever else it does: its own
// settings available to everything it registers, a server, an HTTP transport, and the secret gate
// on /mcp. AddToolServer is that plus the error rule, for a server that offers the agent things to
// call.
//
// Being a tool server and being a channel server are independent facts about a server, which is why
// these are separate calls rather than one call with a flag: a dual-role server asks for both, and
// the once-only filter means it still ends up with one.
public static class ToolServerExtensions
{
    public static IMcpServerBuilder AddMcpHost<TSettings>(this IServiceCollection services, TSettings settings)
        where TSettings : class, IMcpHostSettings
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        return services
            .AddSingleton(settings)
            .AddSingleton<IStartupFilter>(new McpSecretGate(settings.Mcp.SharedSecret))
            .AddMcpServer()
            .WithHttpTransport();
    }

    // The error shape is the caller's to supply: the envelope the nine tool servers answer with
    // lives in Infrastructure, which this project must not reference. The rule about which
    // exceptions reach it is not the caller's.
    public static IMcpServerBuilder AddToolServer<TSettings>(
        this IServiceCollection services,
        TSettings settings,
        Func<Exception, CallToolResult>? errorResult = null)
        where TSettings : class, IMcpHostSettings =>
        services.AddMcpHost(settings).AddCallToolErrorFilter(errorResult);
}