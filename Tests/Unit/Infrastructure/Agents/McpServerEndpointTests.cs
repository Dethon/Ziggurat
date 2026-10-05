using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// A secret that is set to nothing is no secret. The dial presents whatever the endpoint carries, so
// the endpoint is the one place that says so — an empty one must not go out as a bare "Bearer ".
public class McpServerEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnsetSecret_IsNoSecret_WhereverTheEndpointCameFrom(string? unset)
    {
        McpServerEndpoint.Configured("http://mcp-vault:8080/mcp", unset).Secret.ShouldBeNull();
        McpServerEndpoint.Dynamic("http://192.168.1.20:8099/mcp", unset).Secret.ShouldBeNull();
    }

    [Fact]
    public void ASetSecret_IsCarriedAsGiven()
    {
        McpServerEndpoint.Dynamic("http://192.168.1.20:8099/mcp", "s3cret").Secret.ShouldBe("s3cret");
    }
}