using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSugary.Store;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.Agents;

// A registry read outside any conversation — what a built app reads its viewer's data through — over
// the wire, against servers in process: built for a person of a household and one of its agents, it
// mounts what a turn of that agent would and signs every call as that person.
public sealed class AgentReadRegistriesTests
{
    private const string Household = "0123456789abcdef0123456789abcdef";

    private static readonly Account Fran = new(Household, "fran");

    // Her and Him share a household and keep their own logs.
    private static readonly HouseholdSugaryPeople _people = new(
    [
        new SugaryPerson(SugaryServer.Her.UserId, "Her", ReadsEveryLog: false),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", ReadsEveryLog: false)
    ]);

    [Fact]
    public async Task EachPersonsRegistry_ReadsTheirOwnSugaryRows()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        await SaveMealAsync(sugary, SugaryServer.Her, "her lentils");
        await SaveMealAsync(sugary, SugaryServer.Him, "his paella");
        var households = new FakeHouseholds();
        households.Add(SugaryServer.Her.TenantId, Agents(new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [sugary.Endpoint], EnabledFeatures = ["filesystem"]
        }));
        var registries = Registries(households, new SharedServers { Sugary = sugary.Endpoint });

        (await NotesAsync(registries, SugaryServer.Her)).ShouldBe(["her lentils"]);
        (await NotesAsync(registries, SugaryServer.Him)).ShouldBe(["his paella"]);
    }

    // What a turn of the agent mounts, read off the prompt its model is sent, and what a registry for
    // one of its household's people mounts, for every state of a household server: reached where set
    // up and connected, offline where set up and not, and neither where never set up. A shared server
    // the household's features turned off is mounted by neither.
    [Theory]
    [InlineData(true, true, new[] { "/notes", "/vault" }, new string[0])]
    [InlineData(true, false, new[] { "/notes" }, new[] { "vault" })]
    [InlineData(false, true, new[] { "/notes" }, new string[0])]
    public async Task ARegistry_MountsWhatATurnOfTheAgentWould(
        bool setUp, bool connected, string[] mounted, string[] offline)
    {
        await using var notes = await FileSystemServerAsync("notes");
        await using var schedules = await FileSystemServerAsync("schedules");
        await using var vault = await FileSystemServerAsync("vault");
        await using var model = await FakeOpenRouter.StartAsync();
        var households = new FakeHouseholds();
        households.Add(Household, Agents(new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m", EnabledFeatures = ["filesystem"],
            McpServerEndpoints = [notes.Endpoint, schedules.Endpoint, "vault"]
        }), new FeaturesSection { Scheduling = false }, ownServers: setUp ? ["vault"] : []);
        var shared = new SharedServers { Scheduling = schedules.Endpoint };
        var online = connected ? new Dictionary<string, string> { ["vault"] = vault.Endpoint } : [];

        await TurnAsync(households, shared, online, model);
        await using var read = await Registries(households, shared, online)
            .BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);

        var prompt = model.Requests[^1].SystemPrompt.ShouldNotBeNull();
        string[] all = ["/notes", "/schedules", "/vault"];
        all.Where(mount => prompt.Contains($"- `{mount}` —")).ShouldBe(mounted);
        read.Registry.GetMounts().Select(m => m.MountPoint).Order().ShouldBe(mounted);
        read.Offline.ShouldBe(offline);
    }

    // A household server going offline or coming back is seen by the next build, as by a turn's next
    // start: the registry built before says it is stale, and the one built after reaches what is there.
    [Fact]
    public async Task AServerGoingOfflineOrComingBack_IsSeenByTheNextBuild()
    {
        await using var vault = await FileSystemServerAsync("vault");
        var households = new FakeHouseholds();
        households.Add(Household, Agents(Jonas("vault")), ownServers: ["vault"]);
        var online = new Dictionary<string, string> { ["vault"] = vault.Endpoint };
        var registries = Registries(households, new SharedServers(), online);

        await using var connected = await registries.BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);
        Mounts(connected).ShouldBe(["/vault"]);
        (await connected.IsStaleAsync(CancellationToken.None)).ShouldBeFalse();

        online.Remove("vault");
        (await connected.IsStaleAsync(CancellationToken.None)).ShouldBeTrue();
        await using var offline = await registries.BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);
        Mounts(offline).ShouldBeEmpty();
        offline.Offline.ShouldBe(["vault"]);

        online["vault"] = vault.Endpoint;
        (await offline.IsStaleAsync(CancellationToken.None)).ShouldBeTrue();
        await using var back = await registries.BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);
        Mounts(back).ShouldBe(["/vault"]);
        back.Offline.ShouldBeEmpty();
    }

    // The household changing the agent since the build — a feature turned off, the agent removed — makes
    // the registry stale as it makes an open conversation's agent; a change to another agent does not.
    [Fact]
    public async Task AnAgentTheHouseholdChangedSinceTheBuild_MakesTheRegistryStale()
    {
        await using var notes = await FileSystemServerAsync("notes");
        var households = new FakeHouseholds();
        var nabu = Jonas() with { Id = "nabu", Name = "Nabu" };
        households.Add(Household, Agents(Jonas(notes.Endpoint), nabu));
        await using var read = await Registries(households, new SharedServers { Scheduling = notes.Endpoint })
            .BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);

        households.Add(Household, Agents(Jonas(notes.Endpoint), nabu with { Name = "Voz" }));
        (await read.IsStaleAsync(CancellationToken.None)).ShouldBeFalse();

        households.Add(Household, Agents(Jonas(notes.Endpoint), nabu), new FeaturesSection { Scheduling = false });
        (await read.IsStaleAsync(CancellationToken.None)).ShouldBeTrue();

        households.Add(Household, Agents(nabu));
        (await read.IsStaleAsync(CancellationToken.None)).ShouldBeTrue();
    }

    // A turn of an agent offered no filesystem tools mounts nothing, whatever its servers publish.
    [Fact]
    public async Task AnAgentOfferedNoFilesystemTools_MountsNothing()
    {
        await using var notes = await FileSystemServerAsync("notes");
        var households = new FakeHouseholds();
        households.Add(Household, Agents(Jonas(notes.Endpoint) with { EnabledFeatures = [] }));

        await using var read = await Registries(households, new SharedServers())
            .BuildAsync(Viewer(Fran), "jonas", CancellationToken.None);

        Mounts(read).ShouldBeEmpty();
    }

    // Only the household's own agents: a person's registered agent is found for its owner alone.
    [Fact]
    public async Task AnAgentTheHouseholdDoesNotHave_IsRefused_NamingIt()
    {
        var households = new FakeHouseholds();
        households.Add(Household, Agents(Jonas()));
        var custom = new CustomAgentRegistry();
        await custom.AddAsync(Fran, Jonas() with { Id = "custom-1" });
        var registries = new AgentReadRegistries(
            new AgentDefinitionProvider(new HouseholdAgentSource(households, new SharedServers()), custom),
            McpTestSecret.Credentials, TimeProvider.System, callTokens: McpTestSecret.CallTokens);

        foreach (var agent in new[] { "nabu", "custom-1" })
        {
            (await Should.ThrowAsync<AgentNotFoundException>(() => registries.BuildAsync(Viewer(Fran), agent, CancellationToken.None)))
                .AgentId.ShouldBe(agent);
        }
    }

    // A registry is read by a person: the guest and nobody are no viewer of a household's data.
    [Fact]
    public async Task TheGuestAndNobody_ReadNothing()
    {
        var households = new FakeHouseholds();
        households.Add(Household, Agents(Jonas()));
        var registries = Registries(households, new SharedServers());

        foreach (var viewer in new[] { IdentityClaims.Guest(Household, AppChannels.Apps), IdentityClaims.Nobody(AppChannels.Apps) })
        {
            await Should.ThrowAsync<ArgumentException>(() => registries.BuildAsync(viewer, "jonas", CancellationToken.None));
        }
    }

    // The meals' notes the person's registry reads at /sugary, every call signed as they are.
    private static async Task<IReadOnlyList<string?>> NotesAsync(AgentReadRegistries registries, Account person)
    {
        await using var read = await registries.BuildAsync(Viewer(person), "jonas", CancellationToken.None);
        var sugary = read.Registry.Resolve("/sugary/meals.parquet").ShouldBeOfType<FsResult<FileSystemResolution>.Ok>().Value
            .As(read.Caller);

        var query = await sugary.Backend.QueryAsync("", "select notes from meals", 10, CancellationToken.None);
        var answer = query.ShouldBeOfType<FsResult<FsQueryResult>.Ok>(query is FsResult<FsQueryResult>.Err e ? e.Error.Message : null).Value;
        return [.. answer.Rows.Select(row => row[0]?.GetValue<string>())];
    }

    private static AgentDefinition Jonas(params string[] endpoints) => new()
    {
        Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = endpoints, EnabledFeatures = ["filesystem"]
    };

    private static IReadOnlyList<string> Mounts(ReadRegistry read) =>
        [.. read.Registry.GetMounts().Select(m => m.MountPoint).Order()];

    // A turn of the agent for her, as the household's conversation would run it.
    private static async Task TurnAsync(
        FakeHouseholds households, SharedServers shared, IReadOnlyDictionary<string, string> online, FakeOpenRouter model)
    {
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IThreadStateStore>())
            .AddSingleton(Mock.Of<IHouseholdKeyReports>())
            .AddSingleton<IOwnServers>(new InProcessOwnServers(online))
            .AddSingleton(McpTestSecret.CallTokens)
            .AddSingleton(McpTestSecret.Credentials)
            .BuildServiceProvider();
        var factory = new MultiAgentFactory(
            services, Definitions(households, shared), new OpenRouterConfig { ApiUrl = model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry([]));
        await using var agent = await factory.CreateAsync(
            new AgentKey("conv-1", "jonas"), Fran.UserId, "jonas", Mock.Of<IToolApprovalHandler>(), Fran, Household);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hello")]).ToListAsync();
    }

    // A server serving one filesystem, mounted at /<name>.
    private static Task<RunningServer> FileSystemServerAsync(string name) =>
        InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(new NamedMount(name));
            services.AddToolServer(new ProbeSettings()).AddFileSystemTools<NamedMount>().AddFileSystemResource<NamedMount>();
        });

    // Who a built app's viewer is, as the agent verifies their `apps` assertion.
    private static IdentityClaims Viewer(Account person) =>
        IdentityClaims.Person(person.UserId, person.TenantId, AppChannels.Apps, ["member"]);

    private static async Task SaveMealAsync(SugaryServer server, Account person, string notes)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.Meals.Add(new Meal
        {
            Id = Guid.NewGuid(),
            EatenAt = DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            Notes = notes,
            Outcome = MealOutcome.High,
            FirstEstimate = new Interval(30, 40, 55),
            Items = [],
            CarbsLow = 30,
            Carbs = 40,
            CarbsHigh = 55
        });
        await db.SaveChangesAsync();
    }

    private static AgentsSection Agents(params AgentDefinition[] definitions) => new()
    {
        Agents = [.. definitions.Select(d => new HouseholdAgent { Id = d.Id, Definition = d })]
    };

    private static AgentDefinitionProvider Definitions(FakeHouseholds households, SharedServers shared) =>
        new(new HouseholdAgentSource(households, shared), new CustomAgentRegistry());

    private static AgentReadRegistries Registries(
        FakeHouseholds households, SharedServers shared, IReadOnlyDictionary<string, string>? online = null) =>
        new(
            Definitions(households, shared),
            McpTestSecret.Credentials,
            TimeProvider.System,
            new InProcessOwnServers(online ?? new Dictionary<string, string>()),
            McpTestSecret.CallTokens);

    private sealed record ProbeSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private sealed class NamedMount(string name) : FileSystemBackendBase
    {
        public override string FilesystemName => name;

        public override string DescribeMount => $"The {name}.";

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult(Glob([]));
    }

    // A household's configuration as the accounts service would answer the agent.
    private sealed class FakeHouseholds : IHouseholdConfiguration
    {
        private readonly Dictionary<string, AgentHouseholdConfig> _households = [];

        public void Add(
            string id, AgentsSection agents, FeaturesSection? features = null, IReadOnlyList<string>? ownServers = null) =>
            _households[id] = new AgentHouseholdConfig(
                id, false, new HouseholdSettings { Name = "Casa", TimeZone = "UTC" }, agents,
                features ?? new FeaturesSection(), new SpacesSection(), "household-key", OwnServers: ownServers);

        public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
            Task.FromResult(_households[householdId]);

        public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AppAsksSection> AppAsksAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}