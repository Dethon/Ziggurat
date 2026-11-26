using Domain.Security;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// An endpoint as the deployment's settings write it — an agent definition's, a channel's — read once:
// a bare server name is a household's own server, reached through its tunnel and presenting nothing; a
// URL presents the credential the deployment holds for the server it reaches; anything else presents
// nothing, and its dial is what refuses it.
public sealed class McpServerEndpointTests
{
    private static string? Credentials(ServerAddress server) => server.Name == "mcp-vault" ? "vault-secret" : null;

    [Fact]
    public void AServerName_IsAHouseholdsOwnServer_PresentingNothing()
    {
        var endpoint = McpServerEndpoint.Of("vault", Credentials);

        endpoint.NamesOwnServer.ShouldBeTrue();
        endpoint.Secret.ShouldBeNull();
        endpoint.Origin.ShouldBe(McpEndpointOrigin.Configured);
    }

    [Fact]
    public void AUrl_PresentsTheCredentialForTheServerItReaches()
    {
        var endpoint = McpServerEndpoint.Of("http://mcp-vault:8080/mcp", Credentials);

        endpoint.NamesOwnServer.ShouldBeFalse();
        endpoint.Secret.ShouldBe("vault-secret");
        endpoint.Server.ShouldBe(ServerAddress.FromUrl("http://mcp-vault:8080/mcp"));
    }

    [Fact]
    public void AnAddressThatIsNeither_PresentsNothing()
    {
        var endpoint = McpServerEndpoint.Of("not a url", Credentials);

        endpoint.NamesOwnServer.ShouldBeFalse();
        endpoint.Secret.ShouldBeNull();
        endpoint.Server.ShouldBeNull();
    }

    // The name a built app's manifest declares a server by (`asks`) is the one its tools carry
    // (mcp__<name>__…): a household's server by its own name, before and after its tunnel is resolved,
    // and a URL's server by host and port. Every such name passes the manifest's shape check.
    [Theory]
    [InlineData("vault", "vault")]
    [InlineData("http://mcp-sugary:8080/mcp", "mcp-sugary-8080")]
    [InlineData("https://tools.example.org/mcp", "tools.example.org")]
    [InlineData("http://127.0.0.1:5000/mcp", "127.0.0.1-5000")]
    public void AnEndpointsName_IsWhatItsToolsAreQualifiedWith_AndAManifestMayDeclareIt(string address, string name)
    {
        var endpoint = McpServerEndpoint.Of(address, Credentials);

        endpoint.Qualifier.ShouldBe(name);
        ServerAddress.IsQualifier(name).ShouldBeTrue();
    }

    [Fact]
    public void AResolvedHouseholdServer_KeepsTheNameItWasDeclaredBy()
    {
        var resolved = McpServerEndpoint.Relayed(ServerAddress.Tunnelled("fedcba9876543210", "vault"), new HttpClient());

        resolved.Qualifier.ShouldBe(McpServerEndpoint.OwnServer("vault").Qualifier);
    }

    [Fact]
    public void AnAddressThatIsNeither_HasNoName()
    {
        McpServerEndpoint.Of("not a url", Credentials).Qualifier.ShouldBeNull();
    }
}