using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

[McpServerToolType]
public class MetaEchoTool
{
    [McpServerTool(Name = "echo_meta")]
    [Description("Returns the request _meta as JSON text")]
    public static string McpRun(RequestContext<CallToolRequestParams> context)
        => context.Params?.Meta?.ToJsonString() ?? "null";
}

public class MetaEchoServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var (port, app) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<MetaEchoTool>();
            var built = builder.Build();
            built.MapMcp("/mcp");
            return built;
        });
        _host = app;
        McpEndpoint = $"http://127.0.0.1:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}