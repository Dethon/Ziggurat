using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// Whether an endpoint is handed a deployment credential at all. A registered agent's endpoints are
// whatever its registration said, so the deployment's list must name the very server, never one
// that only reads like it.
public class DeploymentEndpointsTests
{
    private static readonly DeploymentEndpoints _deployment = new(["http://mcp-vault:8080/mcp"]);

    [Theory]
    [InlineData("http://mcp-vault:8080/mcp")]
    [InlineData("http://MCP-VAULT:8080/mcp")]
    public void AServerTheDeploymentNamed_IsNamed(string address) =>
        _deployment.Names(address).ShouldBeTrue();

    [Theory]
    [InlineData("http://mcp-vault:8080@elsewhere/mcp")]
    [InlineData("http://someone@mcp-vault:8080/mcp")]
    [InlineData("http://mcp-vault:8081/mcp")]
    [InlineData("https://mcp-vault:8080/mcp")]
    [InlineData("http://mcp-vault:8080/other")]
    [InlineData("mcp-vault")]
    public void AnAddressThatOnlyReadsLikeIt_IsNotNamed(string address) =>
        _deployment.Names(address).ShouldBeFalse();

    [Fact]
    public void AConfiguredAddressCarryingCredentials_NamesNothing() =>
        new DeploymentEndpoints(["http://someone@mcp-vault:8080/mcp"])
            .Names("http://someone@mcp-vault:8080/mcp").ShouldBeFalse();
}