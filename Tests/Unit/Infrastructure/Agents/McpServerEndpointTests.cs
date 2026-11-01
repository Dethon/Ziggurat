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
}