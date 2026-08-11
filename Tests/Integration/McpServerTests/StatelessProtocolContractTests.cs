using System.Net;
using McpServerHomeAssistant.Modules;
using McpServerIdealista.Modules;
using McpServerPrinter.Modules;
using McpServerSandbox.Modules;
using McpServerTimers.Modules;
using McpServerVault.Modules;
using McpServerWebSearch.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using HaSettings = McpServerHomeAssistant.Settings;
using IdealistaSettings = McpServerIdealista.Settings;
using PrinterSettings = McpServerPrinter.Settings.PrinterSettings;
using SandboxSettings = McpServerSandbox.Settings;
using TimerSettings = McpServerTimers.Settings.TimerSettings;
using VaultSettings = McpServerVault.Settings;
using WebSearchSettings = McpServerWebSearch.Settings;

namespace Tests.Integration.McpServerTests;

// The stateless-protocol pin for the servers that have no channel_receive to pin it through:
// websearch (where the diagnostic Stateless = false experiment originally lived) and the six pure
// tool servers. Their tools key per-conversation state off each call's _meta now, but nothing else
// in the suite would notice a single server setting Stateless = false — that renegotiates all the
// way down to 2025-11-25 and quietly resurrects per-session behaviour on that server alone. Same
// reasoning as ChannelReceiveContractTests' per-channel pin: one standalone guard would leave the
// other servers free to drift.
public class StatelessProtocolContractTests
{
    // One row per tool server, driving the REAL registration entry point — the ConfigModule that
    // ships — with placeholder settings. Every module registers its clients lazily (typed
    // HttpClients, singleton factories), so nothing dials out during initialize.
    public static TheoryData<string, Action<IServiceCollection>> Servers => new()
    {
        {
            "vault",
            services => services.ConfigureMcp(new VaultSettings.McpSettings
            {
                VaultPath = "/tmp", AllowedExtensions = []
            })
        },
        {
            "sandbox",
            services => services.ConfigureMcp(new SandboxSettings.McpSettings
            {
                ContainerRoot = "/tmp",
                HomeDir = "/tmp",
                DefaultTimeoutSeconds = 5,
                MaxTimeoutSeconds = 10,
                OutputCapBytes = 1024,
                AllowedExtensions = []
            })
        },
        {
            "websearch",
            services => services.ConfigureMcp(new WebSearchSettings.McpSettings
            {
                BraveSearch = new WebSearchSettings.BraveSearchConfiguration { ApiKey = "x" }
            })
        },
        {
            "idealista",
            services => services.ConfigureMcp(new IdealistaSettings.McpSettings
            {
                Idealista = new IdealistaSettings.IdealistaConfiguration { ApiKey = "x", ApiSecret = "x" }
            })
        },
        {
            "homeassistant",
            services => services.ConfigureMcp(new HaSettings.McpSettings
            {
                HomeAssistant = new HaSettings.HomeAssistantConfiguration
                {
                    BaseUrl = "http://ha.invalid", Token = "x"
                }
            })
        },
        {
            "printer",
            services => services.ConfigurePrinter(new PrinterSettings { PrinterUri = "ipp://printer.invalid" })
        },
        {
            "timers",
            services => services.ConfigureTimers(new TimerSettings())
        }
    };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ToolServer_NegotiatesTheStatelessProtocol(
        string serverId, Action<IServiceCollection> configureServer)
    {
        var port = TestPort.GetAvailable();
        var app = await StartServerAsync(port, configureServer);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            client.NegotiatedProtocolVersion.ShouldBe(
                "2026-07-28", $"{serverId} must stay on the stateless protocol");
            client.SessionId.ShouldBeNull($"{serverId} must not mint an Mcp-Session-Id");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // Boots a tool server from its own ConfigModule, so the transport options under test are
    // byte-for-byte what ships. Module-registered workers (a printer spool pump, a timer fire
    // loop) reach for infrastructure that is not here and are stripped; the web host's own hosted
    // service predates the module and must survive, or Kestrel never listens.
    private static async Task<WebApplication> StartServerAsync(
        int port, Action<IServiceCollection> configureServer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

        var hostServices = builder.Services.Count;
        configureServer(builder.Services);

        var moduleWorkers = builder.Services
            .Skip(hostServices)
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToArray();
        foreach (var worker in moduleWorkers)
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }
}