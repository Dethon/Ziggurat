using System.Diagnostics;
using Infrastructure.Agents;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// Two rules for dialling, decided by where the endpoint came from and by nothing else. A container
// in the compose file being down is a bug and fails the session, as it always has. A machine that
// registered itself being asleep is Tuesday, and costs only its own mount.
// See docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md.
public class ThreadSessionDialPolicyTests(McpVaultServerFixture vault) : IClassFixture<McpVaultServerFixture>
{
    // Nothing is listening here, and nothing ever will be: the port is taken and released, so the
    // dial is refused rather than left hanging — which is what an outpost's machine looks like once
    // its network has gone but its registration has not yet lapsed.
    private static McpServerEndpoint Dead(McpEndpointOrigin origin) =>
        new($"http://localhost:{TestPort.GetAvailable()}/mcp", origin);

    [Fact]
    public async Task ADeadDynamicEndpoint_LeavesTheSessionBuiltFromTheRest()
    {
        await using var session = await BuildAsync(
            McpServerEndpoint.Configured(vault.McpEndpoint),
            Dead(McpEndpointOrigin.Dynamic));

        session.ClientManager.Clients.Count.ShouldBe(1);
        session.ClientManager.Tools.ShouldNotBeEmpty();
        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
    }

    // The same shape with the origin flipped, so the rule cannot be "the second endpoint is
    // forgiven" or "one live endpoint is enough".
    [Fact]
    public async Task ADeadConfiguredEndpoint_FailsTheSession()
    {
        await Should.ThrowAsync<Exception>(() => BuildAsync(
            McpServerEndpoint.Configured(vault.McpEndpoint),
            Dead(McpEndpointOrigin.Configured)));
    }

    // A dynamic endpoint is dialled once. The retry around a configured dial sleeps two, four and
    // eight seconds before giving up, and a laptop with its lid shut would charge that to every
    // session build — on the path a person is waiting on. Nothing retries a dynamic endpoint
    // inside a session; the next session build asks the registry again.
    [Fact]
    public async Task ADeadDynamicEndpoint_DoesNotChargeTheSessionTheRetryBackoff()
    {
        var dialing = Stopwatch.StartNew();

        await using var session = await BuildAsync(
            McpServerEndpoint.Configured(vault.McpEndpoint),
            Dead(McpEndpointOrigin.Dynamic));

        dialing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        session.ClientManager.Clients.Count.ShouldBe(1);
    }

    // Every endpoint dynamic and every one dead is still a session, with no filesystem and no
    // tools rather than an exception: an agent whose only outposts are asleep still answers.
    [Fact]
    public async Task EveryDynamicEndpointBeingDead_StillBuildsASession()
    {
        await using var session = await BuildAsync(
            Dead(McpEndpointOrigin.Dynamic),
            Dead(McpEndpointOrigin.Dynamic));

        session.ClientManager.Clients.ShouldBeEmpty();
        session.FileSystemRegistry.ShouldBeNull();
    }

    private static Task<ThreadSession> BuildAsync(params McpServerEndpoint[] endpoints) =>
        ThreadSession.CreateAsync(
            endpoints,
            "dial-policy-test",
            "test-user",
            "the agent under test",
            [],
            new HashSet<string> { "text_read" },
            null,
            CancellationToken.None);
}