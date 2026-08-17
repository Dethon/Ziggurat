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
    private const string Secret = "s3cret";

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

        endpoints.Endpoints.ShouldBe([_vault]);
        endpoints.Outposts.ShouldBeEmpty();
        registry.Asked.ShouldBe(0);
    }

    [Fact]
    public async Task AnOptedInAgent_GetsEveryLiveOutpostAsADynamicEndpoint()
    {
        var endpoints = await ComposeAsync(new RecordingRegistry(_laptop), usesOutposts: true);

        endpoints.Endpoints.ShouldBe([_vault, McpServerEndpoint.Dynamic(_laptop.Endpoint, Secret)]);
        endpoints.Outposts.ShouldBe(["laptop"]);
    }

    // Mount order decides a name collision, and the configured filesystems have to be mounted
    // first for the existing mount to be the one that wins.
    [Fact]
    public async Task ConfiguredEndpoints_ComeFirst()
    {
        var endpoints = await ComposeAsync(
            new RecordingRegistry(_laptop, _laptop with { Name = "desktop" }), usesOutposts: true);

        endpoints.Endpoints.Take(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Configured);
        endpoints.Endpoints.Skip(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Dynamic);
    }

    [Fact]
    public async Task WithNoMachinesRegistered_TheListIsUnchanged()
    {
        (await ComposeAsync(new RecordingRegistry(), usesOutposts: true)).Endpoints.ShouldBe([_vault]);
    }

    // A host with no registry at all — every test host, and any deployment that never turned
    // outposts on — has the same answer as an agent that did not opt in.
    [Fact]
    public async Task WithNoRegistryAtAll_TheListIsUnchanged()
    {
        (await ComposeAsync(registry: null, usesOutposts: true)).Endpoints.ShouldBe([_vault]);
    }

    // A turn that could still be answered from the deployment's own filesystems must not fail
    // because a machine's registration could not be looked up.
    [Fact]
    public async Task ARegistryThatCannotBeRead_CostsTheSessionItsOutpostsAndNothingElse()
    {
        (await ComposeAsync(new UnreachableRegistry(), usesOutposts: true)).Endpoints.ShouldBe([_vault]);
    }

    // The verdict is per outpost, and a mount belonging to the deployment's own filesystem is
    // nobody's registration to write on.
    [Fact]
    public async Task ASessionBuild_WritesEachOutpostsVerdictAndNobodyElses()
    {
        var registry = new RecordingRegistry(_laptop, _laptop with { Name = "desktop" });

        await OutpostEndpoints.RecordVerdictsAsync(
            Access(registry),
            recordsVerdicts: true,
            outposts: ["laptop", "desktop"],
            mounted: ["vault", "sandbox", "laptop", "desktop"],
            shadowed: ["desktop"],
            NullLogger.Instance,
            CancellationToken.None);

        registry.Verdicts.ShouldBe(new Dictionary<string, OutpostVerdict>
        {
            ["laptop"] = OutpostVerdict.Mounted,
            ["desktop"] = OutpostVerdict.Shadowed
        });
    }

    // "The hub could not reach you" is not a verdict on a mount, and calling it shadowed would
    // name the wrong problem — so an outpost the build never reached a decision about is left as
    // it was.
    [Fact]
    public async Task AnOutpostThatCouldNotBeDialled_KeepsWhateverVerdictItHad()
    {
        var registry = new RecordingRegistry(_laptop);

        await OutpostEndpoints.RecordVerdictsAsync(
            Access(registry), recordsVerdicts: true, outposts: ["laptop"], mounted: ["vault"], shadowed: [],
            NullLogger.Instance, CancellationToken.None);

        registry.Verdicts.ShouldBeEmpty();
    }

    // A session that built must not fail because the machine it built from cannot be told about it.
    [Fact]
    public async Task ARegistryThatCannotBeWritten_DoesNotFailTheSession()
    {
        await Should.NotThrowAsync(() => OutpostEndpoints.RecordVerdictsAsync(
            Access(new UnreachableRegistry()), recordsVerdicts: true, outposts: ["laptop"], mounted: ["laptop"],
            shadowed: [],
            NullLogger.Instance, CancellationToken.None));
    }

    private static Task<ComposedEndpoints> ComposeAsync(
        IOutpostRegistry? registry, bool usesOutposts) =>
        OutpostEndpoints.ComposeAsync(
            [_vault], Access(registry), usesOutposts, NullLogger.Instance, CancellationToken.None);

    private static OutpostAccess? Access(IOutpostRegistry? registry) =>
        registry is null ? null : new OutpostAccess(registry, Secret);

    private sealed class RecordingRegistry(params OutpostRegistration[] live) : IOutpostRegistry
    {
        public int Asked { get; private set; }

        public Dictionary<string, OutpostVerdict> Verdicts { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<OutpostRegistration>>(live);
        }

        public Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<OutpostVerdict?> KeepAliveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<OutpostVerdict?>(OutpostVerdict.Unknown);

        public Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default)
        {
            Verdicts[name] = verdict;
            return Task.CompletedTask;
        }

        public Task<bool> DeregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class UnreachableRegistry : IOutpostRegistry
    {
        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the registry's store is not there");

        public Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<OutpostVerdict?> KeepAliveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<OutpostVerdict?>(OutpostVerdict.Unknown);

        public Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default) =>
            throw new InvalidOperationException("the registry's store is not there");

        public Task<bool> DeregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}