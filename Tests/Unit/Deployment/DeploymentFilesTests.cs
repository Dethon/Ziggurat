using Shouldly;

namespace Tests.Unit.Deployment;

// The reader the deployment conformance checks stand on, run against the real files: if it
// misreads them, every check built on it proves nothing.
public class DeploymentFilesTests
{
    [Fact]
    public void TheCompose_ReadsEachServicesPortsEnvironmentVolumesAndSecretsFile()
    {
        var compose = DeploymentFiles.Compose();

        var caddy = compose.Service("caddy");
        caddy.Ports.ShouldContain(p => p.Container == "443");

        var sandbox = compose.Service("mcp-sandbox");
        sandbox.TakesWholeSecretsFile.ShouldBeFalse("the sandbox is handed its secret by name");
        sandbox.Environment.ShouldContainKey("TZ", "a merged anchor is part of the environment");
        sandbox.Volumes.ShouldContain(v => v.EndsWith(":/home/sandbox_user", StringComparison.Ordinal));

        compose.Service("agent").Environment.ShouldContainKey("CORS__ALLOWEDORIGINS__0", "a list of merged anchors is read too");
        compose.Service("agent").Environment.ShouldContainKey("TZ");
    }

    // A service names its networks as a list or as a map; one naming none is on compose's default
    // network, and one on the host's network is on none of them.
    [Fact]
    public void TheCompose_ReadsEachServicesNetworks()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("agent").Networks.ShouldContain("jackbot");
        compose.Service("plex").Networks.ShouldBeEmpty("a service on the host's network is on no compose network");
        ComposeService.From("bare", new YamlDotNet.RepresentationModel.YamlMappingNode()).Networks.ShouldBe(["default"]);
    }

    [Fact]
    public void TheCaddyfile_ReadsEachHostsRoutes()
    {
        var caddy = DeploymentFiles.Caddyfile();

        var host = caddy.Host("assistants.herfluffness.com");
        host.Routes.ShouldContain(r => r.Matcher == "/hubs/*" && r.Upstream == "mcp-channel-signalr:8080");
        host.Routes.ShouldContain(r => r.Matcher == null && r.Upstream == "webui:8080");
    }
}