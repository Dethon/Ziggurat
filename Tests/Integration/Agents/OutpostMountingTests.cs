using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// The payoff, at the seam that decides it: a machine that registered itself is dialled and mounted
// when a session is built, and a machine whose name is already some other mount's is shadowed
// rather than replacing it.
//
// The fixtures stand in for machines. What an outpost is, at this seam, is an endpoint whose origin
// is dynamic — where it came from and the order it is dialled in are exactly what this exercises.
[Collection("MultiFileSystem")]
public class OutpostMountingTests(MultiFileSystemFixture machines, McpVaultServerFixture vault)
    : IClassFixture<McpVaultServerFixture>
{
    [Fact]
    public async Task AnOptedInAgent_MountsALiveOutpost()
    {
        var endpoints = await ComposeAsync(usesOutposts: true, Registered("notes", machines.NotesEndpoint));

        await using var session = await BuildAsync(endpoints);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault", "notes"], ignoreOrder: true);
    }

    // Nothing is opted in by default, so the same registration reaches an agent that did not ask
    // for it and changes nothing.
    [Fact]
    public async Task AnAgentThatDidNotOptIn_MountsNoneOfThem()
    {
        var endpoints = await ComposeAsync(usesOutposts: false, Registered("notes", machines.NotesEndpoint));

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
            await ComposeAsync(spec, Registered("notes", machines.NotesEndpoint)));

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault", "notes"], ignoreOrder: true);
    }

    // The parent is the ceiling: a worker that asks for machines its parent cannot see gets none,
    // so delegating never reaches somewhere asking directly could not.
    [Fact]
    public async Task ASubAgentWhoseParentIsNotOptedIn_MountsNoneOfThem()
    {
        var spec = SubAgentSpec(parentUsesOutposts: false, ownDefinitionUsesOutposts: true);

        await using var session = await BuildAsync(
            await ComposeAsync(spec, Registered("notes", machines.NotesEndpoint)));

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
    }

    // A stranger's machine cannot shadow the vault. The registration is perfectly valid and the
    // dial succeeds — two clients come up — but the mount point is already taken, so the outpost
    // is simply not there and the existing mount is untouched. Decided by mount order, which is
    // why configured endpoints are composed first.
    [Fact]
    public async Task AnOutpostWhoseNameIsAlreadyAMountsName_IsShadowed()
    {
        var endpoints = await ComposeAsync(usesOutposts: true, Registered("vault", vault.McpEndpoint));

        await using var session = await BuildAsync(endpoints);

        session.ClientManager.Clients.Count.ShouldBe(2);
        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.Name).ShouldBe(["vault"]);
        session.ShadowedNames.ShouldBe(["vault"]);
    }

    // The verdict a session build produces, written back onto the registration so the next
    // keepalive can carry it to the machine. This is the only moment it is knowable.
    [Theory]
    [InlineData("notes", OutpostVerdict.Mounted)]
    [InlineData("vault", OutpostVerdict.Shadowed)]
    public async Task TheBuild_WritesEachOutpostsVerdictOntoItsRegistration(
        string name, OutpostVerdict expected)
    {
        var registry = new StubRegistry([
            Registered(name, name == "vault" ? vault.McpEndpoint : machines.NotesEndpoint)
        ]);
        var access = new OutpostAccess(registry, "s3cret");
        var composed = await OutpostEndpoints.ComposeAsync(
            [McpServerEndpoint.Configured(vault.McpEndpoint)], access, usesOutposts: true,
            logger: null, CancellationToken.None);

        await using var session = await BuildAsync(composed);
        await OutpostEndpoints.RecordVerdictsAsync(
            access, composed.Outposts, session.MountedNames, session.ShadowedNames,
            logger: null, CancellationToken.None);

        registry.Verdicts[name].ShouldBe(expected);
    }

    private static OutpostRegistration Registered(string name, string endpoint) =>
        new() { Name = name, Endpoint = endpoint };

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
            new OutpostAccess(new StubRegistry(live), "s3cret"),
            usesOutposts,
            logger: null,
            CancellationToken.None);

    // The spec's own endpoints and its own opt-in, so what a subagent mounts is decided by what
    // the projection put on it rather than by anything this test restates.
    private static Task<ComposedEndpoints> ComposeAsync(
        AgentSpec spec, params OutpostRegistration[] live) =>
        OutpostEndpoints.ComposeAsync(
            spec.McpServerEndpoints,
            new OutpostAccess(new StubRegistry(live), "s3cret"),
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