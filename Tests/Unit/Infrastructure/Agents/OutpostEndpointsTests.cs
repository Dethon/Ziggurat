using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Outposts;
using Domain.Security;
using Domain.Tools;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// Which agents can see outposts is a deliberate choice, not a default: an agent that exists to
// search for downloads has no business reaching a person's laptop, and a new machine appearing on
// the network must not silently widen what any agent can touch. Which outposts it sees are its
// household's, the ones whose tunnels are open when the session is built.
public class OutpostEndpointsTests
{
    private const string Household = "household-1";

    private static readonly McpServerEndpoint _vault =
        McpServerEndpoint.Configured("http://mcp-vault:8080/mcp");

    private static readonly LiveOutpost _laptop = Outpost("laptop");

    [Fact]
    public void AnAgentThatDidNotOptIn_NeverEvenAsks()
    {
        var outposts = new RecordingOutposts(_laptop);

        var endpoints = Compose(outposts, usesOutposts: false);

        endpoints.Endpoints.ShouldBe([_vault]);
        endpoints.Outposts.ShouldBeEmpty();
        outposts.AskedFor.ShouldBeEmpty();
    }

    [Fact]
    public void AnOptedInAgent_GetsItsHouseholdsLiveOutpostsAsDynamicEndpoints()
    {
        var outposts = new RecordingOutposts(_laptop);

        var endpoints = Compose(outposts, usesOutposts: true);

        endpoints.Endpoints.ShouldBe([_vault, _laptop.Endpoint]);
        endpoints.Endpoints[1].Origin.ShouldBe(McpEndpointOrigin.Dynamic);
        endpoints.Outposts.ShouldBe([_laptop]);
        outposts.AskedFor.ShouldBe([Household]);
    }

    // Mount order decides a name collision, and the configured filesystems have to be mounted
    // first for the existing mount to be the one that wins.
    [Fact]
    public void ConfiguredEndpoints_ComeFirst()
    {
        var endpoints = Compose(new RecordingOutposts(_laptop, Outpost("desktop")), usesOutposts: true);

        endpoints.Endpoints.Take(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Configured);
        endpoints.Endpoints.Skip(1).ShouldAllBe(e => e.Origin == McpEndpointOrigin.Dynamic);
    }

    [Fact]
    public void WithNoOutpostOpen_TheListIsUnchanged()
    {
        Compose(new RecordingOutposts(), usesOutposts: true).Endpoints.ShouldBe([_vault]);
    }

    // A host with no outposts at all — every test host but theirs — has the same answer as an agent
    // that did not opt in.
    [Fact]
    public void WithNoOutpostsAtAll_TheListIsUnchanged()
    {
        Compose(outposts: null, usesOutposts: true).Endpoints.ShouldBe([_vault]);
    }

    // A definition may name the outpost's tunnel outright; the machine is then the definition's, and
    // dialling it a second time as an outpost would only shadow its own mount.
    [Fact]
    public void AnOutpostTheDefinitionAlreadyNames_IsNotAddedTwice()
    {
        var named = _laptop.Endpoint with { Origin = McpEndpointOrigin.Configured };

        var endpoints = OutpostEndpoints.Compose([named], new RecordingOutposts(_laptop), usesOutposts: true, Household);

        endpoints.Endpoints.ShouldBe([named]);
        endpoints.Outposts.ShouldBeEmpty();
    }

    // A machine whose tunnel was open and which then did not answer is the one absence worth trying
    // again, and the registry is where that is remembered — otherwise the model asks for
    // outpost:laptop, is told the path does not exist, and looks for a spelling mistake in a name
    // that was right.
    [Fact]
    public void DeclareUnreachable_AnOpenMachineThatWasNotDialled_IsRememberedAsUnavailable()
    {
        var registry = new RecordingAbsences();

        OutpostEndpoints.DeclareUnreachable(registry, [_laptop], dialled: []);

        var (mountPoint, state, detail) = registry.Declared.ShouldHaveSingleItem();
        mountPoint.ShouldBe("outpost:laptop");
        state.ShouldBe(CapabilityState.Unavailable);
        detail.ShouldContain("did not answer");
    }

    [Fact]
    public void DeclareUnreachable_AMachineThatAnswered_IsNotRememberedAsAnything()
    {
        var registry = new RecordingAbsences();

        OutpostEndpoints.DeclareUnreachable(registry, [_laptop], dialled: [_laptop.Endpoint.Server!]);

        registry.Declared.ShouldBeEmpty();
    }

    // The mount name alone proves nothing: a configured server dialled under the same name is not the
    // machine, so the machine's own server is what was dialled or was not.
    [Fact]
    public void DeclareUnreachable_AnotherServerDialledUnderTheSameName_DoesNotVouchForTheMachine()
    {
        var registry = new RecordingAbsences();

        OutpostEndpoints.DeclareUnreachable(
            registry, [_laptop], dialled: [ServerAddress.FromUrl("http://laptop:8080/mcp")!]);

        registry.Declared.ShouldHaveSingleItem().MountPoint.ShouldBe("outpost:laptop");
    }

    // A session with no mounts at all has no registry to tell, and a build must not fall over
    // because there was nothing to explain to.
    [Fact]
    public void DeclareUnreachable_NoRegistry_DoesNothing()
    {
        Should.NotThrow(() => OutpostEndpoints.DeclareUnreachable(null, [_laptop], dialled: []));
    }

    private static LiveOutpost Outpost(string name) =>
        new(name, McpServerEndpoint.RelayedOutpost(
            ServerAddress.Tunnelled(Household, OutpostServerName.For(name)), new HttpClient()));

    private static ComposedEndpoints Compose(IOutposts? outposts, bool usesOutposts) =>
        OutpostEndpoints.Compose([_vault], outposts, usesOutposts, Household);

    private sealed class RecordingOutposts(params LiveOutpost[] live) : IOutposts
    {
        public List<string?> AskedFor { get; } = [];

        public IReadOnlyList<LiveOutpost> Live(string? tenantId)
        {
            AskedFor.Add(tenantId);
            return live;
        }
    }

    private sealed class RecordingAbsences : IVirtualFileSystemRegistry
    {
        public List<(string MountPoint, CapabilityState State, string Detail)> Declared { get; } = [];

        public void DeclareAbsence(string mountPoint, CapabilityState state, string detail) =>
            Declared.Add((mountPoint, state, detail));

        public void Mount(FileSystemMount mount, IFileSystemBackend backend) { }

        public FsResult<FileSystemResolution> Resolve(string virtualPath) =>
            throw new NotSupportedException();

        public IReadOnlyList<FileSystemMount> GetMounts() => [];
    }
}