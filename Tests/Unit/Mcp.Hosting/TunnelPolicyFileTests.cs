using Agent.Modules;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// How a tunnel is kept alive and brought back is one answer for every server built on the hosting
// library and for the relay at the other end: a shared policy file beside its type in Domain, read
// into the gate settings each already binds, ahead of the environment that carries a site's hub and
// credential.
public class TunnelPolicyFileTests
{
    private sealed record HostSettings
    {
        public McpGateSettings Mcp { get; init; } = new();
    }

    public static TheoryData<string> Hosts => ["server", "agent"];

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EveryTunnelEnd_ReadsTheSharedFileBeforeTheEnvironment(string host)
    {
        var configBuilder = new ConfigurationManager();
        configBuilder.AddEnvironmentVariables();
        var tunnel = host == "server"
            ? configBuilder.BindSettings<HostSettings>().Mcp.Tunnel
            : configBuilder.GetSettings().Mcp.Tunnel;
        var sources = configBuilder.Sources.ToList();

        var file = sources.FindIndex(source => source is JsonConfigurationSource json && json.Path == TunnelSettings.FileName);
        var environment = sources.FindLastIndex(source => source is EnvironmentVariablesConfigurationSource);

        file.ShouldBeGreaterThanOrEqualTo(0, $"the {host} should read {TunnelSettings.FileName}");
        file.ShouldBeLessThan(environment, $"the {host} should read the environment after the shared file");
        configBuilder["mcp:tunnel:reconnectAtMost"].ShouldBe("00:01:00");
        tunnel.ReconnectAtMost.ShouldBe(TimeSpan.FromMinutes(1));
        tunnel.IsConfigured.ShouldBeFalse("a server is dialled as before until a site gives it a hub and a credential");
    }
}