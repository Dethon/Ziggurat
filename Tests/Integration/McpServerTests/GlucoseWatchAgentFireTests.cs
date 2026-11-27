using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.Sugary.Watches;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain;

namespace Tests.Integration.McpServerTests;

// A glucose watch's fire through the agent as the deployment runs it: written through the real Sugary
// server by her turn, fired by the server's own engine on a reading below its level, read off the agent's
// connection to Sugary's channel, and handed to the monitor with the shipped channel defaults — under
// which a message on `sugary` that names no agent goes to the no-tools estimator — the household's agents
// built by the real factory against the fake model. The fire names the agent that wrote the watch, so it
// runs as Jonas, acts for her, and its reply goes where the watch delivers, never to Sugary's channel.
public sealed class GlucoseWatchAgentFireTests : IAsyncLifetime
{
    private const string Word = "GLUCOSELOW";

    private static readonly string _watch = $$"""
        {"name":"Low","triggers":[{"trigger":"glucose","below":70}],
         "effects":[{"kind":"prompt","prompt":"Reply with only the word {{Word}}"}],"deliverTo":["telegram"]}
        """;

    private static readonly HouseholdSugaryPeople _people = new([new SugaryPerson(SugaryServer.Her.UserId, "Her", false)]);

    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    [Fact]
    public async Task AGlucoseFire_RunsOnTheWatchesAgent_AsItsWriter_AndRepliesWhereTheWatchDelivers()
    {
        await using var server = await SugaryServer.StartAsync(
            sugaryPeople: _people, configure: s => s with { Delivery = TestDelegations.Delivery });
        await WriteAsync(server, TurnOf(SugaryServer.Her));
        var fire = await FireAsync(server);
        fire.ChannelId.ShouldBe(ChannelIds.Sugary);

        var sugary = new FakeChannelConnection { ChannelId = ChannelIds.Sugary };
        var telegram = new FakeChannelConnection { ChannelId = ChannelIds.Telegram, ConversationIdToReturn = "tg-conv" };
        sugary.WriteUnsigned(fire);
        sugary.Complete();
        telegram.Complete();
        var definitions = Definitions();
        var monitor = new ChatMonitor(
            [sugary, telegram],
            Factory(definitions),
            new ChatThreadResolver(),
            NoOpMetricsPublisher.Instance,
            null,
            NullLogger<ChatMonitor>.Instance,
            TestAssertions.Verifier(),
            agentDefaults: ShippedAgentDefaults(),
            delegations: TestDelegations.Verifier(),
            agentDefinitions: definitions);

        await monitor.Monitor(CancellationToken.None);

        var request = _model.Requests.ShouldHaveSingleItem();
        request.SystemPrompt.ShouldNotBeNull().ShouldContain("You are Jonas.");
        request.SystemPrompt.ShouldNotContain("You are Sugary.");
        telegram.CreatedConversations.ShouldHaveSingleItem().AgentId.ShouldBe("jonas");
        telegram.CreatedFor.ShouldHaveSingleItem().ShouldNotBeNull().Account.ShouldBe(SugaryServer.Her);
        string.Concat(telegram.SentReplies.Where(r => r.ConversationId == "tg-conv").Select(r => r.Content))
            .ShouldContain(Word);
        sugary.SentReplies.ShouldBeEmpty("the reply is the watch's delivery, never Sugary's own channel");
        sugary.CreatedConversations.ShouldBeEmpty();
    }

    // Her own turn with Jonas, as the agent's conversation group leaves it: the identity it verified.
    private static ConversationContext TurnOf(Account person) =>
        new("jonas", "conv-1", person.UserId, new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, person.UserId, person),
            Identity: IdentityClaims.Person(person.UserId, person.TenantId, ChannelIds.SignalR, ["member"]));

    // As the agent's filesystem writes a watch for her turn, signing the delegation for it.
    private static async Task WriteAsync(SugaryServer server, ConversationContext turn)
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint));
        var result = await new McpFileSystemBackend(client, SugaryWatchFileSystem.Name, null).As(turn)
            .CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>(result is FsResult<FsCreateResult>.Err e ? e.Error.Message : null);
    }

    // Her glucose falling below the watch's level, judged by the server's own engine after two polls, and
    // the fire the agent's connection reads off Sugary's channel for it.
    private static async Task<ChannelMessage> FireAsync(SugaryServer server)
    {
        var agent = await server.AgentAsync();
        var engine = server.Services.GetRequiredService<GlucoseWatchEngine>();
        var start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        await PollAsync(server, engine, start, 100);
        await PollAsync(server, engine, start + TimeSpan.FromMinutes(1), 60);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await agent.Messages.FirstAsync(wait.Token);
    }

    private static async Task PollAsync(SugaryServer server, GlucoseWatchEngine engine, DateTimeOffset at, double mgPerDl)
    {
        await server.SaveAsync(new GlucoseReading
        {
            UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, At = at, MgPerDl = mgPerDl,
            Source = GlucoseSource.Current
        });
        var reading = new SensorReading(at, mgPerDl, GlucoseTrend.Steady);
        await engine.PolledAsync(
            new GlucosePoll(SugaryServer.Her, [reading], new GlucoseLinkState(at - TimeSpan.FromDays(1), at, null, null, null), reading),
            CancellationToken.None);
    }

    // Her household with the two starting templates, Jonas its default, as the accounts service lists it.
    private static AgentDefinitionProvider Definitions() =>
        new(new HouseholdAgentSource(new OneHousehold(SugaryServer.Her.TenantId), new SharedServers()), new CustomAgentRegistry());

    private MultiAgentFactory Factory(AgentDefinitionProvider definitions)
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Mock.Of<IThreadStateStore>())
            .AddSingleton(Mock.Of<IHouseholdKeyReports>())
            .AddSingleton<IOwnServers>(new InProcessOwnServers(new Dictionary<string, string>()))
            .AddSingleton(McpTestSecret.CallTokens)
            .BuildServiceProvider();
        return new MultiAgentFactory(
            services, definitions, new OpenRouterConfig { ApiUrl = _model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry([]));
    }

    // The channel defaults the agent ships with: Sugary's channel to its estimator.
    private static AgentDefaults ShippedAgentDefaults()
    {
        var defaults = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "Agent", "appsettings.json"))
            .Build()
            .GetSection("agentDefaults").Get<AgentDefaults>()!;
        defaults.For(ChannelIds.Sugary).ShouldBe("sugary", "the trap this test is about");
        return defaults;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ziggurat.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Ziggurat.sln not found above test directory");
    }

    private sealed class OneHousehold(string id) : IHouseholdConfiguration
    {
        private readonly AgentHouseholdConfig _config = new(
            id, false, new HouseholdSettings { Name = "Casa", TimeZone = "UTC" }, new AgentsSection(), new FeaturesSection(),
            new SpacesSection(), "household-key");

        public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) => Task.FromResult(_config);

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