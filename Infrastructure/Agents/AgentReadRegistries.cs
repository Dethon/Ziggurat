using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Security.Signing;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The mounts of one of a household's agents, read outside any conversation: what a built app reads its
// viewer's data through. Built for a person of that household by the one build a turn's session goes
// through (SessionReach) over the same definition — withheld by the household's features, and by the
// own servers it has not set up or that are offline as of this build — so it mounts what a turn of
// that agent would, and every call through it is signed as that person.
//
// Only the household's own agents: a person's registered agent is found for its owner alone, and apps
// are built by the household's. Never its outposts, whatever the definition says: a machine is
// somebody's own computer, reached for the person at it, and an app reads the household's data.
public sealed class AgentReadRegistries(
    IAgentDefinitionProvider definitions,
    McpCredentials credentials,
    IOwnServers? ownServers = null,
    CallTokens? callTokens = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly McpPromptCache _promptCache = new(TimeProvider.System, TimeSpan.FromSeconds(60));

    // `viewer` is the identity the agent verified, as a turn's is (AssertionVerifier.VerifyTurnAsync's
    // admission), never one read off a request: its household is the household read, and its person
    // the one every call is signed as. The guest and nobody read nothing.
    public async Task<ReadRegistry> BuildAsync(IdentityClaims viewer, string agentId, CancellationToken ct)
    {
        var account = viewer.Account
                      ?? throw new ArgumentException("a registry is read by a person of a household", nameof(viewer));
        var household = await definitions.ForHouseholdAsync(account.TenantId, ct);
        var definition = household.Find(agentId) ?? throw new AgentNotFoundException(agentId, account.TenantId);

        Task<HouseholdAgents> HouseholdNow(CancellationToken ct) => definitions.ForHouseholdAsync(account.TenantId, ct);
        var reach = new SessionReach(
            AgentSpecProjection.Endpoints(definition, credentials, DeploymentEndpoints.Of(household)),
            account.TenantId,
            household.WithheldServers,
            HouseholdNow,
            ownServers,
            outposts: null,
            usesOutposts: false);
        var reached = await reach.ResolveAsync(ct);
        var session = await SessionReach.BuildAsync(reached, new SessionShape(
            $"{definition.Name}-read",
            account.UserId,
            definition.Description ?? "",
            DomainTools: [],
            AgentSpecProjection.ExtractFilesystemEnabledTools(definition.EnabledFeatures),
            loggerFactory,
            _promptCache,
            ReadImages: null,
            Bridge: null,
            definition.NoTools,
            WithheldTools: new HashSet<string>(),
            callTokens,
            TimeZones.Named(household.TimeZone) ?? TimeZoneInfo.Local), ct);

        return new ReadRegistry(
            session, reach, reached, HouseholdNow, household.AsBuilt(definition.Id), ReadRegistry.CallerFor(viewer, definition.Id),
            new ToolPermission(new ToolPatternMatcher(definition.WhitelistPatterns).IsMatch),
            FileSystemToolFeature.Offers(AgentSpecProjection.ExtractFilesystemEnabledTools(definition.EnabledFeatures)));
    }
}

// One build of an agent's mounts for one person, owning the clients it dialled until disposed. The
// registry's backends act for nobody until bound (FileSystemResolution.As, VfsCall): bind them to
// Caller, the person this registry was built for.
public sealed class ReadRegistry : IAsyncDisposable
{
    // What the caller's conversation id says when there is none: a server scoping state by
    // conversation keeps a read's apart from every turn's.
    public const string NoConversation = "outside-a-conversation";

    private readonly ThreadSession _session;
    private readonly SessionReach _reach;
    private readonly ServerReach _reached;
    private readonly Func<CancellationToken, Task<HouseholdAgents>> _household;
    private readonly AgentAsBuilt? _builtFrom;

    internal ReadRegistry(
        ThreadSession session, SessionReach reach, ServerReach reached,
        Func<CancellationToken, Task<HouseholdAgents>> household, AgentAsBuilt? builtFrom, ConversationContext caller,
        ToolPermission permission, Func<string, bool> offered)
    {
        Permission = permission;
        Offered = offered;
        _session = session;
        _reach = reach;
        _reached = reached;
        _household = household;
        _builtFrom = builtFrom;
        Caller = caller;
    }

    // Empty where a turn would have none: an agent offered no filesystem tools mounts nothing.
    public IVirtualFileSystemRegistry Registry => _session.FileSystemRegistry ?? new VirtualFileSystemRegistry();

    public ConversationContext Caller { get; }

    // What the agent's file tools would run with nobody asked: its whitelist, as its approval client
    // decides it. Nothing is remembered outside a conversation, so the whitelist is all there is.
    public ToolPermission Permission { get; }

    // Whether the agent is offered a file tool, by its name: one it was never given is never run for it.
    public Func<string, bool> Offered { get; }

    // One read's bridge call over these mounts, for this person, at the bridge's per-file ceiling. No
    // token: nothing finds it again, and it lives as long as the read.
    public VfsCall Call(long maxFileBytes) =>
        new("", Registry, Permission, Offered, [.. Registry.GetMounts().Where(VfsCall.IsServed)], Caller, maxFileBytes);

    // The household's own servers named by the agent and not reached by this build.
    public IReadOnlyList<string> Offline => _reached.OwnServers.Offline;

    // Whether a build now would differ: the household defines the agent otherwise (a feature turned
    // off, the agent removed — DisposableAgent.IsStaleAsync's question), or its servers changed (one came
    // online, went offline or reconnected — a turn's start's). Both are lookups, never a dial.
    public async Task<bool> IsStaleAsync(CancellationToken ct) =>
        (await _household(ct)).AsBuilt(Caller.AgentId) != _builtFrom
        || !(await _reach.ResolveAsync(ct)).ReachesTheSameAs(_reached);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    // Whom every call is signed as (CallIdentity): the person alone, out of whatever envelope the
    // verified identity came in.
    internal static ConversationContext CallerFor(IdentityClaims viewer, string agentId)
    {
        var person = IdentityClaims.Person(viewer.Subject, viewer.TenantId, viewer.ChannelId, viewer.Roles);
        return new ConversationContext(
            agentId, NoConversation, person.Subject, new ReplyTarget(person.ChannelId, null),
            Principal: person.Principal, Identity: person);
    }
}