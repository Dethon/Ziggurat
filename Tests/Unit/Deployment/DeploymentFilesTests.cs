using Shouldly;
using Tests.E2E.Fixtures;

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

        var sugary = compose.Service("mcp-sugary");
        sugary.TakesWholeSecretsFile.ShouldBeFalse("the server is handed its secrets by name");
        sugary.Environment.ShouldContainKey("TZ", "a merged anchor is part of the environment");
        sugary.Volumes.ShouldContain(v => v.EndsWith(":/data", StringComparison.Ordinal));

        compose.Service("agent").Environment.ShouldContainKey("CORS__ALLOWEDORIGINS__0", "a list of merged anchors is read too");
        compose.Service("agent").Environment.ShouldContainKey("TZ");
        compose.Service("lemonade").Devices.ShouldContain("/dev/dri:/dev/dri");
    }

    // A service names its networks as a list or as a map; one naming none is on compose's default
    // network, and one on the host's network is on none of them.
    [Fact]
    public void TheCompose_ReadsEachServicesNetworks()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("agent").Networks.ShouldContain("jackbot");
        DeploymentFiles.KitCompose().Service("plex").Networks.ShouldBeEmpty("a service on the host's network is on no compose network");
        ComposeService.From("bare", new YamlDotNet.RepresentationModel.YamlMappingNode()).Networks.ShouldBe(["default"]);
    }

    // The tenant kit is read the same way, with what it is built from and the networks it declares,
    // and anchors merged as a list.
    [Fact]
    public void TheKitsCompose_ReadsEachServicesBuildNetworkModeAndTheNetworksItDeclares()
    {
        var kit = DeploymentFiles.KitCompose();

        kit.Networks.ShouldContainKey("kit");
        kit.Networks["kit"].ShouldBeFalse("a network declared with nothing is the project's own");
        kit.Service("mcp-vault").BuildContext.ShouldBe("${REPOSITORY_PATH}");
        kit.Service("mcp-vault").Dockerfile.ShouldBe("McpServerVault/Dockerfile");
        kit.Service("camoufox").Dockerfile.ShouldBe("Dockerfile", "a context with no dockerfile builds its own");
        kit.Service("mcp-vault").Environment.ShouldContainKey("TZ");
        kit.Service("mcp-vault").Environment.ShouldContainKey("MCP__TUNNEL__HUB", "both anchors of a merged list");
        kit.Service("music-assistant").NetworkMode.ShouldBe("host");
        kit.Service("redis").Image.ShouldBe("redis/redis-stack-server:latest");
        DeploymentFiles.KitPlaceholders().ShouldContainKey("DEPLOYMENT_HOST");
        DeploymentFiles.Variables(DeploymentFiles.KitDirectory).ShouldContain("DEPLOYMENT_HOST", "a defaulted or required ${VAR} is read too");
    }

    [Fact]
    public void TheCaddyfile_ReadsEachHostsRoutes()
    {
        var caddy = DeploymentFiles.Caddyfile();

        var host = caddy.Host("assistants.herfluffness.com");
        host.Routes.ShouldContain(r => r.Matcher == "/hubs/*" && r.Upstream == "mcp-channel-signalr:8080");
        host.Routes.ShouldContain(r => r.Matcher == null && r.Upstream == "webui:8080");
    }

    // A check that nothing ships proves nothing if the list of what ships is short.
    [Fact]
    public void TheShippedFiles_AreTheComposeDirectoryEveryHostsSettingsAndPolicyFilesAndEveryDockerfile()
    {
        var shipped = DeploymentFiles.Shipped()
            .Select(f => Path.GetRelativePath(TestHelpers.FindSolutionRoot(), f).Replace('\\', '/'))
            .ToList();

        shipped.ShouldContain("DockerCompose/docker-compose.yml");
        shipped.ShouldContain("DockerCompose/docker-compose.override.smoke.yml");
        shipped.ShouldContain("DockerCompose/.env");
        shipped.ShouldContain("DockerCompose/caddy/Caddyfile");
        shipped.ShouldContain("TenantKit/docker-compose.yml");
        shipped.ShouldContain("TenantKit/.env");
        shipped.ShouldContain("Dockerfile.base-sdk");
        shipped.ShouldContain("Accounts/Dockerfile");
        shipped.ShouldContain("Accounts/appsettings.Local.json");
        shipped.ShouldContain("Agent/appsettings.json");
        shipped.ShouldContain("McpChannelVoice/appsettings.Development.json");
        shipped.ShouldContain("Domain/retention.json");
        shipped.ShouldNotContain(f => f.StartsWith("Tests/", StringComparison.Ordinal));
        shipped.ShouldNotContain(f => f.StartsWith("DockerCompose/volumes/", StringComparison.Ordinal));
        shipped.ShouldNotContain(f => f.StartsWith("TenantKit/volumes/", StringComparison.Ordinal));
        shipped.ShouldNotContain(f => f.Contains("/bin/", StringComparison.Ordinal) || f.Contains("/obj/", StringComparison.Ordinal));
    }
}