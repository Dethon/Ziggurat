using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Prompts;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Deployment;

namespace Tests.Integration.Agents;

// Agents built per household (seam 3), through the real factory and the agent's own OpenRouter client
// against a fake model: a turn runs its household's agent, read from the household's configuration —
// with its name, its custom instructions, its language and its zone in the prompt the model is sent,
// and only the servers and tools its features leave it.
public sealed class MultiAgentFactoryHouseholdTests : IAsyncLifetime
{
    private const string Household = "0123456789abcdef0123456789abcdef";
    private const string Home = "fedcba9876543210fedcba9876543210";
    private const string HouseholdKey = "household-key";

    // Nothing answers here: a session that dialled it would fail to build, and the turn with it.
    private const string UnansweredScheduling = "http://127.0.0.1:9/mcp";

    // A Saturday in UTC, the host's zone here, that is still the Saturday in New York.
    private static readonly DateTimeOffset _now = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeHouseholds _households = new();
    private readonly KeyReports _reports = new();
    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    [Fact]
    public async Task AHouseholdsTurn_RunsItsAgent_WithItsNameInstructionsLanguageAndZoneInThePrompt()
    {
        _households.Add(Household, isHome: false,
            new HouseholdSettings { Name = "Casa Pérez", TimeZone = "America/New_York", Language = "fr" },
            new AgentsSection
            {
                DefaultAgent = "ana",
                Agents = [new HouseholdAgent { Id = "ana", Template = "jonas", Name = "Ana", CustomInstructions = "Sign every answer with a rhyme." }]
            });

        var prompt = await TurnAsync("ana", Household);

        prompt.ShouldContain("You are Ana. " + AgentTemplates.Named("jonas")!.Description);
        prompt.ShouldContain("Sign every answer with a rhyme.");
        prompt.ShouldContain("You always reply in fr");
        prompt.ShouldContain("Today is Saturday, 2026-10-31, in the America/New_York time zone.");
    }

    [Fact]
    public async Task ANewHousehold_HasTheTwoShippedTemplates()
    {
        _households.Add(Household, isHome: false, Settings(), new AgentsSection());
        var definitions = Definitions();

        (await definitions.ForHouseholdAsync(Household)).DefaultAgent.ShouldBe("jonas");
        (await TurnAsync("jonas", Household)).ShouldContain("You are Jonas.");
        (await TurnAsync("sugary", Household)).ShouldContain("You are Sugary.");
    }

    // The home's own agents are its seeded definitions, run as they were defined — nabu with its voice
    // section and its own Spanish — in the deployment's own zone, so with the date as every snapshot
    // holds it. Its servers are left out here, where none of them runs.
    [Fact]
    public async Task TheHomesTurn_RunsItsSeededDefinition()
    {
        var nabu = HomeAgents.Definition("nabu") with { McpServerEndpoints = [] };
        _households.Add(Home, isHome: true, Settings() with { Language = "en" }, new AgentsSection
        {
            DefaultAgent = "nabu", Agents = [new HouseholdAgent { Id = "nabu", Definition = nabu }]
        });

        var prompt = await TurnAsync("nabu", Home);

        prompt.ShouldContain("You are Nabu. " + nabu.Description);
        prompt.ShouldContain(VoicePrompt.Instructions);
        prompt.ShouldEndWith(LanguagePrompt.Build("es"));
        prompt.ShouldContain("Today is Saturday, 2026-10-31.");
    }

    // A turn acting for nobody — a fire whose delegation was not believed — runs the home's agents.
    [Fact]
    public async Task ATurnForNobody_RunsTheHomesAgents()
    {
        _households.Add(Home, isHome: true, Settings(), new AgentsSection
        {
            DefaultAgent = "jack",
            Agents = [new HouseholdAgent { Id = "jack", Template = "jonas", Name = "Jack of the house" }]
        });

        (await TurnAsync("jack", household: null)).ShouldContain("You are Jack of the house.");
    }

    // Scheduling off: its server is never dialled — nothing answers at its address, and the turn runs
    // anyway — and workers off: the model is offered no tool to spawn one.
    [Fact]
    public async Task AFeatureTurnedOff_RemovesItsServerAndItsTools()
    {
        var jonas = new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m",
            McpServerEndpoints = [UnansweredScheduling], EnabledFeatures = ["subagents"]
        };
        _households.Add(Household, isHome: false, Settings(),
            new AgentsSection { Agents = [new HouseholdAgent { Id = "jonas", Definition = jonas }] },
            new FeaturesSection { Scheduling = false, Workers = false });

        await TurnAsync("jonas", Household);

        Tools(_model.Requests[^1]).ShouldNotContain(tool => tool.Contains("subagents"));
    }

    [Fact]
    public async Task AFeatureLeftOn_KeepsItsTools()
    {
        var jonas = new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [], EnabledFeatures = ["subagents"] };
        _households.Add(Household, isHome: false, Settings(),
            new AgentsSection { Agents = [new HouseholdAgent { Id = "jonas", Definition = jonas }] });

        await TurnAsync("jonas", Household);

        Tools(_model.Requests[^1]).ShouldContain(tool => tool.Contains("subagents"));
    }

    // The other half of the server's withholding: left on, the same unanswered address is dialled, and
    // the session that needed it fails before the model is asked anything.
    [Fact]
    public async Task AFeatureLeftOn_DialsItsServer()
    {
        var jonas = new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [UnansweredScheduling] };
        _households.Add(Household, isHome: false, Settings(),
            new AgentsSection { Agents = [new HouseholdAgent { Id = "jonas", Definition = jonas }] });

        await Should.ThrowAsync<Exception>(() => TurnAsync("jonas", Household));

        _model.Requests.ShouldBeEmpty();
    }

    // A worker's profile is the deployment's and names the shared servers it may use; the household
    // that turned one off withholds it from its workers as from its agents.
    [Fact]
    public async Task AWorkerOfAHouseholdThatTurnedAServerOff_DoesNotDialIt()
    {
        var factory = Factory();
        await using var worker = factory.CreateSubAgent(
            new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [UnansweredScheduling] },
            Mock.Of<IToolApprovalHandler>(),
            new SpawnContext("conv-1", "fran", [], UsesOutposts: false, "m", TenantId: Household,
                WithheldServers: [UnansweredScheduling]));

        await worker.RunStreamingAsync([new ChatMessage(ChatRole.User, "go")]).ToListAsync();

        _model.Requests.ShouldHaveSingleItem();
    }

    // Who pays: every model call made for a household goes out on its own OpenRouter key, the home's
    // on the deployment's, and a household that set none is refused before anything is sent.
    [Fact]
    public async Task AHouseholdsTurn_IsSentWithItsOwnKey()
    {
        _households.Add(Household, isHome: false, Settings(), Jonas(), key: HouseholdKey);

        await TurnAsync("jonas", Household);

        _model.Requests.ShouldHaveSingleItem().Authorization.ShouldBe($"Bearer {HouseholdKey}");
    }

    // The home's calls are the deployment's, even where its keys section holds one of its own.
    [Theory]
    [InlineData(null)]
    [InlineData("homes-own-key")]
    public async Task TheHomesTurn_IsSentWithTheDeploymentsKey(string? homesKey)
    {
        _households.Add(Home, isHome: true, Settings(), Jonas(), key: homesKey);

        await TurnAsync("jonas", Home);

        _model.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer deployment-key");
    }

    [Fact]
    public async Task ATurnForNobody_IsSentWithTheDeploymentsKey()
    {
        _households.Add(Home, isHome: true, Settings(), Jonas(), key: null);

        await TurnAsync("jonas", household: null);

        _model.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer deployment-key");
    }

    [Fact]
    public async Task AHouseholdWithNoKey_IsRefusedItsTurn_AndNothingIsSent()
    {
        _households.Add(Household, isHome: false, Settings(), Jonas(), key: null);

        var refused = await Should.ThrowAsync<HouseholdWithoutKeyException>(() => TurnAsync("jonas", Household));

        refused.HouseholdId.ShouldBe(Household);
        _model.Requests.ShouldBeEmpty();
    }

    // A worker runs its parent's model for its parent's household, and so on its parent's key.
    [Fact]
    public async Task AWorkerSpawnedByAHouseholdsAgent_IsSentWithThatHouseholdsKey()
    {
        _households.Add(Household, isHome: false, Settings(), new AgentsSection
        {
            Agents =
            [
                new HouseholdAgent
                {
                    Id = "jonas",
                    Definition = new AgentDefinition
                    {
                        Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [], EnabledFeatures = ["subagents"]
                    }
                }
            ]
        }, key: HouseholdKey);
        _model.Script(FakeOpenRouter.CallingTool(
            $"domain__subagents__{SubAgentRunTool.Name}", """{"subAgentId":"worker","prompt":"go"}"""));

        var approves = new Mock<IToolApprovalHandler>();
        approves.Setup(a => a.RequestApprovalAsync(It.IsAny<ApprovalAsk>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.Approved);

        await TurnAsync("jonas", Household, approves.Object);

        // The parent's call, the worker's, and the parent's again with the worker's answer.
        _model.Requests.Count.ShouldBe(3);
        _model.Requests.ShouldAllBe(request => request.Authorization == $"Bearer {HouseholdKey}");
    }

    [Fact]
    public async Task AWorkerOfTheHome_IsSentWithTheDeploymentsKey()
    {
        await using var worker = Factory().CreateSubAgent(
            new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [] },
            Mock.Of<IToolApprovalHandler>(),
            new SpawnContext("conv-1", "fran", [], UsesOutposts: false, "m", TenantId: Home));

        await worker.RunStreamingAsync([new ChatMessage(ChatRole.User, "go")]).ToListAsync();

        _model.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer deployment-key");
    }

    // A household's own key that OpenRouter refuses in a turn is said to the accounts service, which
    // shows it rejected on the owner's keys page; the turn still fails as it would have.
    [Fact]
    public async Task AHouseholdsOwnKeyOpenRouterRefusesInATurn_IsReportedForThatHousehold()
    {
        _households.Add(Household, isHome: false, Settings(), Jonas(), key: HouseholdKey);
        _model.Refuse(HttpStatusCode.Unauthorized);

        await Should.ThrowAsync<Exception>(() => TurnAsync("jonas", Household));

        await Eventually.Until(() => !_reports.Households.IsEmpty, "the refused key to be reported");
        _reports.Households.ShouldBe([Household]);
    }

    // The deployment's key is nobody's to fix on a keys page, and a key out of credit is not refused.
    [Theory]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.PaymentRequired)]
    public async Task ARefusalThatIsNotAHouseholdsKeyRefused_IsNotReported(bool home, HttpStatusCode status)
    {
        _households.Add(home ? Home : Household, isHome: home, Settings(), Jonas(), key: HouseholdKey);
        _model.Refuse(status);

        await Should.ThrowAsync<Exception>(() => TurnAsync("jonas", home ? Home : Household));

        await Eventually.Settle();
        _reports.Households.ShouldBeEmpty();
    }

    private static AgentsSection Jonas() => new()
    {
        Agents =
        [
            new HouseholdAgent
            {
                Id = "jonas", Definition = new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [] }
            }
        ]
    };

    private async Task<string> TurnAsync(string agentId, string? household, IToolApprovalHandler? approvals = null)
    {
        await using var agent = await Factory().CreateAsync(
            new AgentKey("conv-1", agentId), "fran", agentId, approvals ?? Mock.Of<IToolApprovalHandler>(), tenantId: household);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hello")]).ToListAsync();

        return _model.Requests[^1].SystemPrompt.ShouldNotBeNull();
    }

    private MultiAgentFactory Factory()
    {
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(_now))
            .AddSingleton(Mock.Of<IThreadStateStore>())
            .AddSingleton<IHouseholdKeyReports>(_reports)
            .BuildServiceProvider();

        return new MultiAgentFactory(
            services,
            Definitions(),
            new OpenRouterConfig { ApiUrl = _model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry([new SubAgentToolFeature(new SubAgentRegistryOptions
            {
                SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [] }]
            })]));
    }

    private AgentDefinitionProvider Definitions() =>
        new(new HouseholdAgentSource(_households, new SharedServers { Scheduling = UnansweredScheduling }), new CustomAgentRegistry());

    private static HouseholdSettings Settings() => new() { Name = "Casa", TimeZone = "UTC" };

    private static IReadOnlyList<string> Tools(FakeOpenRouter.Request request) =>
        [.. (request.Body["tools"] as JsonArray ?? []).Select(tool => tool?["name"]?.GetValue<string>() ?? "")];

    // What the accounts service was told of keys refused in use.
    private sealed class KeyReports : IHouseholdKeyReports
    {
        public ConcurrentQueue<string> Households { get; } = new();

        public Task OpenRouterKeyRefusedAsync(string householdId, CancellationToken ct)
        {
            Households.Enqueue(householdId);
            return Task.CompletedTask;
        }
    }

    // A household's configuration as the accounts service would answer the agent, the home asked for
    // by name answered as the home.
    private sealed class FakeHouseholds : IHouseholdConfiguration
    {
        private readonly Dictionary<string, AgentHouseholdConfig> _households = [];

        public void Add(
            string id, bool isHome, HouseholdSettings settings, AgentsSection agents, FeaturesSection? features = null,
            string? key = HouseholdKey) =>
            _households[id] = new AgentHouseholdConfig(
                id, isHome, settings, agents, features ?? new FeaturesSection(), new SpacesSection(), key);

        public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
            Task.FromResult(householdId == HouseholdConfigProtocol.HomeHousehold
                ? _households.Values.Single(h => h.IsHome)
                : _households[householdId]);

        public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}