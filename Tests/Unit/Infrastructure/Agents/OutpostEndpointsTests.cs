using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// Which agents can see outposts is a deliberate choice, not a default: an agent that exists to
// search for downloads has no business reaching a person's laptop, and a new machine appearing on
// the network must not silently widen what any agent can touch.
public class OutpostEndpointsTests
{
    private static readonly McpServerEndpoint _vault =
        McpServerEndpoint.Configured("http://mcp-vault:8080/mcp");

    private static readonly OutpostRegistration _laptop = new()
    {
        Name = "laptop",
        Endpoint = "http://192.168.1.20:8099/mcp"
    };

    [Fact]
    public async Task AnAgentThatDidNotOptIn_NeverEvenAsks()
    {
        var registry = new RecordingRegistry(_laptop);

        var endpoints = await ComposeAsync(registry, usesOutposts: false);

        endpoints.ShouldBe([_vault]);
        registry.Asked.ShouldBe(0);
    }

    [Fact]
    public async Task AnOptedInAgent_GetsEveryLiveOutpostAsADynamicEndpoint()
    {
        var endpoints = await ComposeAsync(new RecordingRegistry(_laptop), usesOutposts: true);

        endpoints.ShouldBe([_vault, McpServerEndpoint.Dynamic(_laptop.Endpoint)]);
    }

    // Mount order decides a name collision, and the configured filesystems have to be mounted
    // first for the existing mount to be the one that wins.
    [Fact]
    public async Task ConfiguredEndpoints_ComeFirst()
    {
        var endpoints = await ComposeAsync(
            new RecordingRegistry(_laptop, _laptop with { Name = "desktop" }), usesOutposts: true);

        endpoints.Take(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Configured);
        endpoints.Skip(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Dynamic);
    }

    [Fact]
    public async Task WithNoMachinesRegistered_TheListIsUnchanged()
    {
        (await ComposeAsync(new RecordingRegistry(), usesOutposts: true)).ShouldBe([_vault]);
    }

    // A host with no registry at all — every test host, and any deployment that never turned
    // outposts on — has the same answer as an agent that did not opt in.
    [Fact]
    public async Task WithNoRegistryAtAll_TheListIsUnchanged()
    {
        (await ComposeAsync(registry: null, usesOutposts: true)).ShouldBe([_vault]);
    }

    // A turn that could still be answered from the deployment's own filesystems must not fail
    // because a machine's registration could not be looked up.
    [Fact]
    public async Task ARegistryThatCannotBeRead_CostsTheSessionItsOutpostsAndNothingElse()
    {
        (await ComposeAsync(new UnreachableRegistry(), usesOutposts: true)).ShouldBe([_vault]);
    }

    private static Task<IReadOnlyList<McpServerEndpoint>> ComposeAsync(
        IOutpostRegistry? registry, bool usesOutposts) =>
        OutpostEndpoints.ComposeAsync(
            [_vault], registry, usesOutposts, NullLogger.Instance, CancellationToken.None);

    private sealed class RecordingRegistry(params OutpostRegistration[] live) : IOutpostRegistry
    {
        public int Asked { get; private set; }

        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<OutpostRegistration>>(live);
        }

        public Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> KeepAliveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> DeregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class UnreachableRegistry : IOutpostRegistry
    {
        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the registry's store is not there");

        public Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> KeepAliveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> DeregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}