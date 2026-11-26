using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The name an agent's server is configured under, which a built app's manifest declares it by (`asks`):
// a shared server its `sharedServers` key, a household's own server its own name. Never its host and port,
// which are the deployment's topology and change under a published app.
public class SharedServersTests
{
    private static readonly SharedServers _shared = new()
    {
        Scheduling = "http://mcp-scheduling:8080/mcp",
        Timers = "http://mcp-timers:8080/mcp",
        Sugary = "http://mcp-sugary:8080/mcp",
        Idealista = "http://mcp-idealista:8080/mcp",
        Apps = "http://mcp-apps:8080/mcp"
    };

    [Theory]
    [InlineData("http://mcp-scheduling:8080/mcp", "scheduling")]
    [InlineData("http://mcp-timers:8080/mcp", "timers")]
    [InlineData("http://mcp-sugary:8080/mcp", "sugary")]
    [InlineData("http://MCP-SUGARY:8080/mcp", "sugary")]
    [InlineData("http://mcp-idealista:8080/mcp", "idealista")]
    [InlineData("http://mcp-apps:8080/mcp", "apps")]
    [InlineData("vault", "vault")]
    [InlineData("homeassistant", "homeassistant")]
    public void AnEndpoint_IsNamedAsItIsConfigured(string endpoint, string name) =>
        _shared.NameOf(endpoint).ShouldBe(name);

    // Compared as servers: another port, path or scheme is another server, and a URL the deployment does not
    // run as a shared server has no name an app could declare.
    [Theory]
    [InlineData("http://mcp-sugary:8081/mcp")]
    [InlineData("http://mcp-sugary:8080/other")]
    [InlineData("https://mcp-sugary:8080/mcp")]
    [InlineData("http://tools.example.org/mcp")]
    [InlineData("not a url")]
    public void AnEndpointThatIsNoSharedServerAndNoServerName_HasNoName(string endpoint) =>
        _shared.NameOf(endpoint).ShouldBeNull();

    // A name picks one server: a household's own server called as a shared one is not declared by it, whether
    // or not this deployment runs that shared server.
    [Theory]
    [InlineData("sugary")]
    [InlineData("apps")]
    [InlineData("idealista")]
    public void AnOwnServerNamedAsASharedOne_HasNoName(string endpoint)
    {
        _shared.NameOf(endpoint).ShouldBeNull();
        new SharedServers().NameOf(endpoint).ShouldBeNull();
    }

    [Fact]
    public void ASharedServerTheDeploymentDoesNotRun_NamesNothing() =>
        new SharedServers().NameOf("http://mcp-sugary:8080/mcp").ShouldBeNull();
}