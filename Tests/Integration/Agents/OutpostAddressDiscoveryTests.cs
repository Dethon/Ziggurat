using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.Integration.Agents;

// A machine that registered itself is somebody's own computer, and everything that keeps it apart
// from the deployment's mounts — never served into a sandbox command, listed as a machine, shadowed
// only by another machine — reads its address, `outpost:<name>`. That address is the machine's own
// word. A binary from before it publishes `/<name>`, which reads as one more deployment mount. Who
// registered an endpoint is what the hub knows for itself, so a mount from a registered endpoint
// that is not at a machine address is not mounted at all.
public class OutpostAddressDiscoveryTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private static Task<RunningServer> ServerPublishing(string name) =>
        InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(new RenderedMount(name, new Dictionary<string, string> { ["a/status.json"] = "{}" }));
            services.AddToolServer(new ProbeSettings(name))
                .AddFileSystemTools<RenderedMount>()
                .AddFileSystemResource<RenderedMount>();
        });

    [Fact]
    public async Task ARegisteredEndpointPublishingADeploymentStyleMount_IsNotMounted()
    {
        await using var server = await ServerPublishing("laptop");
        var registry = new VirtualFileSystemRegistry();

        var refused = await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [server.Client], new HashSet<McpClient> { server.Client }, registry, NullLogger.Instance, CancellationToken.None);

        registry.GetMounts().ShouldBeEmpty();
        refused.ShouldBe(["laptop"]);
    }

    [Fact]
    public async Task TheSameMountFromAConfiguredEndpoint_IsMounted()
    {
        await using var server = await ServerPublishing("laptop");
        var registry = new VirtualFileSystemRegistry();

        var refused = await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [server.Client], new HashSet<McpClient>(), registry, NullLogger.Instance, CancellationToken.None);

        registry.GetMounts().Select(m => m.MountPoint).ShouldBe(["/laptop"]);
        refused.ShouldBeEmpty();
    }
}