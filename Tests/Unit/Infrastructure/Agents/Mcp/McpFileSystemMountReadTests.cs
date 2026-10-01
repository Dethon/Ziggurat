using Domain.DTOs;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.Mcp;

// What discovery makes of a filesystem resource body. Each claim a mount publishes arrives on the
// mount the registry holds, and a server that predates a claim publishes no field, which must read
// as the claim not made.
public class McpFileSystemMountReadTests
{
    [Theory]
    [InlineData("contained", ShellReach.Contained)]
    [InlineData("host", ShellReach.Host)]
    public void APublishedShellReach_IsCarriedOnTheMount(string published, ShellReach expected)
    {
        var mount = McpFileSystemDiscovery.ReadMount(
            $$"""{"name":"box","mountPoint":"/box","description":"d","shellReach":"{{published}}"}""", ["exec"]);

        mount.ShouldNotBeNull().ShellReach.ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"name":"box","mountPoint":"/box","description":"d"}""")]
    [InlineData("""{"name":"box","mountPoint":"/box","description":"d","shellReach":null}""")]
    public void NoShellReach_ReadsAsNone(string json)
    {
        McpFileSystemDiscovery.ReadMount(json, ["exec"]).ShouldNotBeNull().ShellReach.ShouldBeNull();
    }

    // A reach this agent does not know is not a reach it can reason about. Read as none rather than
    // refusing the mount: the mount still works, and the screen skips what it cannot place.
    [Fact]
    public void AnUnknownShellReach_ReadsAsNone()
    {
        McpFileSystemDiscovery.ReadMount(
                """{"name":"box","mountPoint":"/box","description":"d","shellReach":"orbit"}""", [])
            .ShouldNotBeNull().ShellReach.ShouldBeNull();
    }

    [Fact]
    public void TheOtherClaims_AreCarriedBesideIt()
    {
        var mount = McpFileSystemDiscovery.ReadMount(
            """{"name":"box","mountPoint":"/box","description":"d","workspace":"home/u","landingTarget":true}""",
            ["exec"]);

        mount.ShouldNotBeNull();
        mount.Workspace.ShouldBe("home/u");
        mount.IsLandingTarget.ShouldBeTrue();
        mount.Capabilities.ShouldBe(["exec"]);
    }

    [Fact]
    public void ABodyWithNoName_IsNoMount()
    {
        McpFileSystemDiscovery.ReadMount("""{"mountPoint":"/box"}""", []).ShouldBeNull();
    }
}