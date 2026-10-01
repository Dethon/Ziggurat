using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServers;

// A real MCP server over real HTTP, booted from whichever hosting call is under test. The filter
// rules are about what a caller sees, and the only way to see it is to call a tool.
public static class InMemoryMcpServer
{
    // A caller may name the port when it needs the address before the server exists — a connection
    // that must retry until its channel server comes up, say.
    public static async Task<RunningServer> StartAsync(
        Action<IServiceCollection> configure, int? port = null)
    {
        port ??= TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port.Value));
        configure(builder.Services);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var endpoint = $"http://localhost:{port}/mcp";
        // Presented whether or not the server is gated: a server booted from a hosting call asks for
        // it, and one booted from the bare SDK ignores the header.
        var client = await McpClient.CreateAsync(McpTestSecret.Transport(endpoint));

        return new RunningServer(app, client, endpoint);
    }

    public static string Text(CallToolResult result) =>
        string.Join("|", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}

// Endpoint is handed back so a test can point a real client — the agent's own McpChannelConnection,
// say — at this server instead of using the one built above.
public sealed record RunningServer(WebApplication App, McpClient Client, string Endpoint) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

[McpServerToolType]
public sealed class FailingTools
{
    [McpServerTool(Name = "throws")]
    [Description("Test tool that always throws.")]
    public static string Throws() => throw new InvalidOperationException("boom");

    [McpServerTool(Name = "cancels")]
    [Description("Test tool that always cancels, like an aborted long poll.")]
    public static string Cancels() => throw new OperationCanceledException();

    // An HttpClient timeout throws TaskCanceledException (an OperationCanceledException subclass)
    // with no caller cancellation behind it. The tool's own token is never touched.
    [McpServerTool(Name = "times_out")]
    [Description("Test tool that fails like an HttpClient timeout: cancelled, but not by the caller.")]
    public static string TimesOut() => throw new TaskCanceledException();

    // Hangs until the request's own token is cancelled, so a test can drive a genuine
    // caller-cancelled OperationCanceledException instead of a tool-thrown one.
    [McpServerTool(Name = "hangs")]
    [Description("Test tool that hangs until the caller's own token is cancelled.")]
    public static Task Hangs(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);
}
// What a registration added, without booting anything. Counting filters is the one question both
// hosting calls and every real server are asked, so it is written once.
public static class McpServerProbe
{
    public static int CallToolFilterCount(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return CallToolFilterCount(services);
    }

    public static int CallToolFilterCount(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>()
            .Value.Filters.Request.CallToolFilters.Count;
    }
}