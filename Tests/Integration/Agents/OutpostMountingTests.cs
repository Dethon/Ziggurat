using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// The payoff, at the seam that decides it: a machine that registered itself is dialled and mounted
// at its machine address when a session is built, a machine named like one of the deployment's own
// mounts sits beside it, and a machine whose name another machine already has is shadowed rather
// than replacing it.
//
// The machines are real outpost servers, so the address each one publishes is the outpost's own.
// What this exercises is where an endpoint came from and the order it is dialled in.
public class OutpostMountingTests(OutpostMachinesFixture machines, McpVaultServerFixture vault)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<OutpostMachinesFixture>
{
    [Fact]
    public async Task AnOptedInAgent_MountsALiveOutpost()
    {
        var endpoints = await ComposeAsync(usesOutposts: true, Registered("laptop", machines.Laptop));

        await using var session = await BuildAsync(endpoints);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault", "outpost:laptop"], ignoreOrder: true);
    }

    // Nothing is opted in by default, so the same registration reaches an agent that did not ask
    // for it and changes nothing.
    [Fact]
    public async Task AnAgentThatDidNotOptIn_MountsNoneOfThem()
    {
        var endpoints = await ComposeAsync(usesOutposts: false, Registered("laptop", machines.Laptop));

        await using var session = await BuildAsync(endpoints);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
    }

    // Delegation reaches the machine. The spec is the subagent's own, built by the projection from
    // an opted-in parent and an opted-in definition, and what it composes against is the registry
    // rather than anything its parent resolved — so the mount is whatever is live at spawn time.
    [Fact]
    public async Task ASubAgentSpawnedFromAnOptedInParent_MountsALiveOutpost()
    {
        var spec = SubAgentSpec(parentUsesOutposts: true, ownDefinitionUsesOutposts: true);

        await using var session = await BuildAsync(
            await ComposeAsync(spec, Registered("laptop", machines.Laptop)));

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault", "outpost:laptop"], ignoreOrder: true);
    }

    // The parent is the ceiling: a worker that asks for machines its parent cannot see gets none,
    // so delegating never reaches somewhere asking directly could not.
    [Fact]
    public async Task ASubAgentWhoseParentIsNotOptedIn_MountsNoneOfThem()
    {
        var spec = SubAgentSpec(parentUsesOutposts: false, ownDefinitionUsesOutposts: true);

        await using var session = await BuildAsync(
            await ComposeAsync(spec, Registered("laptop", machines.Laptop)));

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
    }

    // A machine named like the vault is a separate machine at an address of its own, so it no
    // longer competes with the vault for the name: both are mounted, each where it is spelled.
    [Fact]
    public async Task AnOutpostNamedLikeAMount_IsMountedBesideIt()
    {
        var endpoints = await ComposeAsync(usesOutposts: true, Registered("vault", machines.Vault));

        await using var session = await BuildAsync(endpoints);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault", "outpost:vault"], ignoreOrder: true);
        session.ShadowedNames.ShouldBeEmpty();
    }

    // The collision that is left. The second machine's registration is perfectly valid and its
    // dial succeeds — three clients come up — but its address is already the first one's, so it is
    // simply not there and the first is untouched. Decided by mount order, not by dial timing.
    [Fact]
    public async Task ASecondOutpostWithTheSameName_IsShadowed()
    {
        var endpoints = await ComposeAsync(
            usesOutposts: true,
            Registered("laptop", machines.Laptop),
            Registered(OutpostMachinesFixture.TwinName, machines.Twin));

        await using var session = await BuildAsync(endpoints);

        session.ClientManager.Clients.Count.ShouldBe(3);
        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault", "laptop"], ignoreOrder: true);
        session.ShadowedNames.ShouldBe([OutpostMachinesFixture.TwinName]);
    }

    // The verdict a session build produces, written back onto each registration so the next
    // keepalive can carry it to the machine. This is the only moment it is knowable.
    [Fact]
    public async Task TheBuild_WritesEachOutpostsVerdictOntoItsRegistration()
    {
        var registry = new StubRegistry([
            Registered("laptop", machines.Laptop),
            Registered(OutpostMachinesFixture.TwinName, machines.Twin),
            Registered("vault", machines.Vault)
        ]);
        var access = Access(registry);
        var composed = await OutpostEndpoints.ComposeAsync(
            [McpServerEndpoint.Configured(vault.McpEndpoint)], access, usesOutposts: true,
            logger: null, CancellationToken.None);

        await using var session = await BuildAsync(composed);
        await OutpostEndpoints.RecordVerdictsAsync(
            access, recordsVerdicts: true, composed.Outposts, session.MountedNames, session.ShadowedNames,
            session.ClientManager.DialledEndpoints, logger: null, CancellationToken.None);

        registry.Verdicts.ShouldBe(new Dictionary<string, OutpostVerdict>
        {
            ["laptop"] = OutpostVerdict.Mounted,
            [OutpostMachinesFixture.TwinName] = OutpostVerdict.Shadowed,
            ["vault"] = OutpostVerdict.Mounted
        });
    }

    // The name alone proves nothing. An outpost registered under a name a configured mount already
    // holds, on a machine that is asleep, must not be told "Mounted" by way of that mount vouching
    // for it — the machine would believe the agent can reach its files, and the collision it is
    // owed would surface only once it woke up. No dial, no verdict.
    [Fact]
    public async Task AnAsleepOutpostNamedLikeAConfiguredMount_IsNotJudgedByThatMount()
    {
        var registry = new StubRegistry([
            Registered("vault", $"http://localhost:{TestPort.GetAvailable()}/mcp")
        ]);
        var access = Access(registry);
        var composed = await OutpostEndpoints.ComposeAsync(
            [McpServerEndpoint.Configured(vault.McpEndpoint)], access, usesOutposts: true,
            logger: null, CancellationToken.None);

        await using var session = await BuildAsync(composed);
        await OutpostEndpoints.RecordVerdictsAsync(
            access, recordsVerdicts: true, composed.Outposts, session.MountedNames, session.ShadowedNames,
            session.ClientManager.DialledEndpoints, logger: null, CancellationToken.None);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
        registry.Verdicts.ShouldBeEmpty();
    }

    // The two sessions the ADR is about, in the order they happen. The parent already has the
    // first laptop among its configured endpoints — a machine can be named there by hand — so the
    // twin is shadowed in its session and its keepalive is owed exactly that. The subagent's
    // endpoint list is not its parent's, so the same machine mounts there — and it writes nothing
    // back, because "did the agent you registered with mount you" is not a question a delegated
    // task gets to answer. Both verdicts are put in and read out through the recording seam;
    // nothing here seeds the registration behind it.
    [Fact]
    public async Task ASubAgentsBuild_LeavesTheVerdictItsParentRecorded()
    {
        const string twin = OutpostMachinesFixture.TwinName;
        var registry = new StubRegistry([Registered(twin, machines.Twin)]);
        var access = Access(registry);

        var parent = await OutpostEndpoints.ComposeAsync(
            [McpServerEndpoint.Configured(machines.Laptop)], access, usesOutposts: true,
            logger: null, CancellationToken.None);
        await using (var parentSession = await BuildAsync(parent))
        {
            await OutpostEndpoints.RecordVerdictsAsync(
                access, recordsVerdicts: true, parent.Outposts,
                parentSession.MountedNames, parentSession.ShadowedNames,
                parentSession.ClientManager.DialledEndpoints,
                logger: null, CancellationToken.None);
        }

        registry.Verdicts[twin].ShouldBe(OutpostVerdict.Shadowed);

        var spec = SubAgentSpec(parentUsesOutposts: true, ownDefinitionUsesOutposts: true);
        var composed = await OutpostEndpoints.ComposeAsync(
            spec.McpServerEndpoints, access, spec.UsesOutposts, logger: null, CancellationToken.None);

        await using var session = await BuildAsync(composed);
        await OutpostEndpoints.RecordVerdictsAsync(
            access, spec.RecordsOutpostVerdicts, composed.Outposts,
            session.MountedNames, session.ShadowedNames, session.ClientManager.DialledEndpoints,
            logger: null, CancellationToken.None);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldContain(twin);
        registry.Verdicts[twin].ShouldBe(OutpostVerdict.Shadowed);
    }

    private static OutpostRegistration Registered(string name, string endpoint) =>
        new() { Name = name, Endpoint = endpoint };

    private static OutpostAccess Access(StubRegistry registry) => new(registry, "s3cret");

    private AgentSpec SubAgentSpec(bool parentUsesOutposts, bool ownDefinitionUsesOutposts) =>
        AgentSpecProjection.ForSubAgent(
            new SubAgentDefinition
            {
                Id = "worker",
                Name = "Worker",
                Model = "test-model",
                McpServerEndpoints = [vault.McpEndpoint],
                UsesOutposts = ownDefinitionUsesOutposts
            },
            new SpawnContext("conv-1", "test-user", [], parentUsesOutposts),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            logger: null);

    private Task<ComposedEndpoints> ComposeAsync(
        bool usesOutposts, params OutpostRegistration[] live) =>
        OutpostEndpoints.ComposeAsync(
            [McpServerEndpoint.Configured(vault.McpEndpoint)],
            Access(new StubRegistry(live)),
            usesOutposts,
            logger: null,
            CancellationToken.None);

    // The spec's own endpoints and its own opt-in, so what a subagent mounts is decided by what
    // the projection put on it rather than by anything this test restates.
    private static Task<ComposedEndpoints> ComposeAsync(
        AgentSpec spec, params OutpostRegistration[] live) =>
        OutpostEndpoints.ComposeAsync(
            spec.McpServerEndpoints,
            Access(new StubRegistry(live)),
            spec.UsesOutposts,
            logger: null,
            CancellationToken.None);

    private static Task<ThreadSession> BuildAsync(ComposedEndpoints composed) =>
        ThreadSession.CreateAsync(
            composed.Endpoints,
            "outpost-mounting-test",
            "test-user",
            "the agent under test",
            [],
            new HashSet<string> { "text_read" },
            null,
            CancellationToken.None);

    private sealed class StubRegistry(OutpostRegistration[] live) : IOutpostRegistry
    {
        public Dictionary<string, OutpostVerdict> Verdicts { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OutpostRegistration>>(live);

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
}