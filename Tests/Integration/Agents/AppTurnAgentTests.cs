using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Security;
using Domain.Skills;
using Domain.Tools.Memory;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain;
using Tests.Unit.Domain.Skills;

namespace Tests.Integration.Agents;

// A built app's ask, as the agent runs it: a message on the deployment's `apps` channel carrying the app
// turn, handed to the monitor with the household's agents built by the real factory against the fake
// model, a real server dialled, the real recall hook and a skill judge. The same person's ordinary turn
// with the same agent, beside it, sends everything the ask must not — the persona, every kind of tool,
// the skills, a recall block — so each absence below is one that could have been there.
public sealed class AppTurnAgentTests : IAsyncLifetime
{
    private const string Household = "fedcba9876543210fedcba9876543210";
    private const string Instructions = "You are the carb estimator of this app. Answer with the grams of carbohydrate.";
    private const string AskText = "How many carbs in a slice of bread?";
    private const string ChatText = "What did I eat yesterday?";
    private const string Remembered = "Prefers answers in grams";

    private static readonly Account _viewer = new(Household, "0123456789abcdef0123456789abcdef");

    private static readonly AppTurn _app = new() { AppId = "a1b2c3d4e5f6", Instructions = Instructions };

    private FakeOpenRouter _model = null!;
    private SugaryServer _server = null!;

    private readonly List<MetricEvent> _published = [];
    private readonly MemoryExtractionQueue _extractions = new();
    private readonly Mock<IEmbeddingService> _embeddings = new();
    private readonly ScriptedJudge _judge = new(_ => new JudgmentOutcome.Absent(AbsenceReason.Error));

    public async Task InitializeAsync()
    {
        _model = await FakeOpenRouter.StartAsync();
        _server = await SugaryServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _model.DisposeAsync();
    }

    [Fact]
    public async Task AnAsk_IsSentTheAppsInstructions_WithNoPersona_NoTools_NoSkills_AndNoMemory()
    {
        var (apps, chat) = await RunAsync(Ask(), Chat());

        var ordinary = RequestFor(ChatText);
        ordinary.SystemPrompt.ShouldNotBeNull().ShouldContain("You are Jonas.");
        ordinary.SystemPrompt.ShouldContain("ALWAYS-SIGN-AS-JONAS");
        ordinary.SystemPrompt.ShouldContain("## Memory System");
        ordinary.SystemPrompt.ShouldContain("## Subagent Delegation");
        ordinary.SystemPrompt.ShouldContain("`/sugary`");
        ordinary.SystemPrompt.ShouldContain("<skill>");
        ordinary.ToolNames.ShouldContain(SkillLoadTool.Name);
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__filesystem__", StringComparison.Ordinal));
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__memory__", StringComparison.Ordinal));
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__subagents__", StringComparison.Ordinal));
        ordinary.Body.ToJsonString().ShouldContain(Remembered);

        var ask = RequestFor(AskText);
        var prompt = ask.SystemPrompt.ShouldNotBeNull();
        prompt.ShouldStartWith(CoreDirectivePrompt.Instructions);
        prompt.ShouldContain(Instructions);
        prompt.ShouldContain("Today is");
        prompt.ShouldNotContain("You are Jonas");
        prompt.ShouldNotContain("ALWAYS-SIGN-AS-JONAS");
        prompt.ShouldNotContain("## Memory System");
        prompt.ShouldNotContain("## Subagent Delegation");
        prompt.ShouldNotContain("## User Context");
        prompt.ShouldNotContain("`/sugary`");
        prompt.ShouldNotContain("<skill>");
        prompt.ShouldNotContain("load_skill");
        (ask.Body["tools"] as JsonArray ?? []).ShouldBeEmpty($"tools went out: {ask.Body["tools"]?.ToJsonString()}");
        ask.Body.ToJsonString().ShouldNotContain(Remembered);
        ask.Body.ToJsonString().ShouldNotContain("Offline household servers");

        // Answered on its own channel, and nowhere else.
        string.Concat(apps.SentReplies.Where(r => r.TurnKey == "ask-turn").Select(r => r.Content)).ShouldNotBeEmpty();
        apps.SentReplies.Last().ContentType.ShouldBe(ReplyContentType.StreamComplete);
        chat.CreatedConversations.ShouldBeEmpty();

        // Recalled for and extracted from the ordinary turn alone; no judge asked which skill the ask needs.
        _embeddings.Verify(e => e.GenerateEmbeddingAsync(It.Is<string>(s => s.Contains(AskText)), It.IsAny<CancellationToken>()), Times.Never);
        _extractions.Complete();
        (await _extractions.ReadAllAsync(CancellationToken.None).ToListAsync())
            .ShouldHaveSingleItem().ConversationId.ShouldBe("chat-1");
        _judge.Asked.Select(r => r.Caller.ConversationId).ShouldNotContain("ask-1");
    }

    // What the ask cost is the household's, under the person who asked, on the apps channel and naming the app.
    [Fact]
    public async Task AnAsksUsage_IsCountedOnTheAppsChannel_UnderTheViewer_NamingTheApp()
    {
        await RunAsync(Ask());

        var usage = Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem();
        usage.Channel.ShouldBe(AppChannels.Apps);
        usage.AppId.ShouldBe(_app.AppId);
        usage.UserId.ShouldBe(_viewer.UserId);
        usage.TenantId.ShouldBe(Household);
        usage.AgentId.ShouldBe("jonas");
    }

    [Fact]
    public async Task AnOrdinaryTurnsUsage_NamesItsChannel_AndNoApp()
    {
        await RunAsync(Chat());

        var usage = Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem();
        usage.Channel.ShouldBe(ChannelIds.SignalR);
        usage.AppId.ShouldBeNull();
    }

    private FakeOpenRouter.Request RequestFor(string text) =>
        _model.Requests.Where(r => r.Path.Contains("responses", StringComparison.Ordinal))
            .Single(r => r.Body["input"]!.ToJsonString().Contains(text, StringComparison.Ordinal));

    private IReadOnlyList<MetricEvent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    private static ChannelMessage Ask() => new()
    {
        ConversationId = "ask-1",
        Content = AskText,
        Sender = "app",
        ChannelId = AppChannels.Apps,
        AgentId = "jonas",
        TurnKey = "ask-turn",
        Principal = new Principal(AppChannels.Apps, _viewer.UserId, _viewer),
        Assertion = TestAssertions.Of(_viewer, AppChannels.Apps),
        AppTurn = _app,
        // Asked for, and never made: an app's conversation is no topic.
        ReviewCopy = new ReviewCopy { Title = "Ask" }
    };

    private static ChannelMessage Chat() => new()
    {
        ConversationId = "chat-1",
        Content = ChatText,
        Sender = "Fran",
        ChannelId = ChannelIds.SignalR,
        AgentId = "jonas",
        Principal = new Principal(ChannelIds.SignalR, _viewer.UserId, _viewer),
        Assertion = TestAssertions.Of(_viewer, ChannelIds.SignalR)
    };

    private async Task<(FakeChannelConnection Apps, FakeChannelConnection Chat)> RunAsync(params ChannelMessage[] messages)
    {
        var apps = new FakeChannelConnection { ChannelId = AppChannels.Apps };
        var chat = new FakeChannelConnection
        {
            ChannelId = ChannelIds.SignalR, HostsReviewCopies = true, ConversationIdToReturn = "topic-1"
        };
        foreach (var message in messages)
        {
            (message.ChannelId == AppChannels.Apps ? apps : chat).WriteUnsigned(message);
        }

        apps.Complete();
        chat.Complete();

        var households = new AgentHouseholds();
        households.Add(Household, [Jonas()], new FeaturesSection { Apps = true });
        var definitions = new AgentDefinitionProvider(
            new HouseholdAgentSource(households, new SharedServers()), new CustomAgentRegistry());

        var monitor = new ChatMonitor(
            [apps, chat],
            Factory(definitions),
            new ChatThreadResolver(),
            Metrics(),
            RecallHook(definitions),
            NullLogger<ChatMonitor>.Instance,
            TestAssertions.Verifier(),
            skillPreloader: new SkillPreloader(_judge, new SkillPreloadSettings { DeadlineMs = 600 }, TimeProvider.System),
            agentDefinitions: definitions,
            standing: new HouseholdAccountStanding(households));

        await monitor.Monitor(CancellationToken.None);
        return (apps, chat);
    }

    // The household's own Jonas, every kind of tool and section an agent can carry: a server with a mount
    // and skills, the file tools, workers and memory, a persona and its owner's instructions.
    private AgentDefinition Jonas() => new()
    {
        Id = "jonas",
        Name = "Jonas",
        Description = "The household's assistant.",
        Model = "deepseek/deepseek-v4.1-flash-1017",
        McpServerEndpoints = [_server.Endpoint],
        EnabledFeatures = ["filesystem", "subagents", "memory"],
        CustomInstructions = "ALWAYS-SIGN-AS-JONAS",
        WhitelistPatterns = ["domain__filesystem*"]
    };

    private MultiAgentFactory Factory(IAgentDefinitionProvider definitions)
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Mock.Of<IThreadStateStore>())
            .AddSingleton(Mock.Of<IHouseholdKeyReports>())
            .AddSingleton<IOwnServers>(new InProcessOwnServers(new Dictionary<string, string>()))
            .AddSingleton(McpTestSecret.Credentials)
            .AddSingleton(McpTestSecret.CallTokens)
            .BuildServiceProvider();
        return new MultiAgentFactory(
            services, definitions, new OpenRouterConfig { ApiUrl = _model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry(
            [
                new SubAgentToolFeature(new SubAgentRegistryOptions
                {
                    SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [] }]
                }),
                new MemoryToolFeature(Mock.Of<IMemoryStore>(), _embeddings.Object)
            ]),
            Metrics());
    }

    // The real recall hook over a person who has something to remember: it recalls for an ordinary turn,
    // so finding nothing of it in the ask's request is the gate, not an empty store.
    private MemoryRecallHook RecallHook(IAgentDefinitionProvider definitions)
    {
        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.HasAnyMemoriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<float[]?>(),
                It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemorySearchResult(new MemoryEntry
                {
                    Id = "mem_1", UserId = _viewer.UserId, Category = MemoryCategory.Preference, Content = Remembered,
                    Importance = 0.9, Confidence = 0.9, CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
                }, 0.9)
            ]);
        _embeddings.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[8]);
        return new MemoryRecallHook(
            store.Object, _embeddings.Object, Mock.Of<IThreadStateStore>(), _extractions, Metrics(), definitions,
            NullLogger<MemoryRecallHook>.Instance, new MemoryRecallOptions());
    }

    private IMetricsPublisher Metrics()
    {
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                lock (_published)
                {
                    _published.Add(e);
                }
            });
        return metrics.Object;
    }
}