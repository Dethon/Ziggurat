using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Monitor;
using Domain.Security;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;

namespace Tests.Unit.Domain.Monitor;

// A built app's ask reaches the agent as a message on the deployment's `apps` channel carrying an app turn.
// The agent believes that descriptor from that connection alone, admits the turn on the viewer's `apps`
// assertion and the household's word, and answers every refusal so the apps server is never left waiting.
public class ChatMonitorAppTurnTests
{
    private const string Household = "fedcba9876543210fedcba9876543210";

    private static readonly Account _viewer = new(Household, "0123456789abcdef0123456789abcdef");

    private static readonly AppTurn _app = new() { AppId = "a1b2c3d4", Instructions = "You estimate carbs." };

    [Fact]
    public async Task AnAppTurnFromTheAppsEndpoint_IsStampedOnTheTurnsMessage_AndActsForTheViewer()
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        var message = run.Agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem();
        message.GetAppTurn().ShouldBe(_app);
        message.GetConversationContext().ShouldNotBeNull().Principal.ShouldNotBeNull().Account.ShouldBe(_viewer);
        run.Factory.Created.ShouldHaveSingleItem().AgentId.ShouldBe("jonas");
        run.Errors.ShouldBeEmpty();
    }

    // Only the apps server's own connection says which app a turn is for: anything else carrying the
    // descriptor is an ordinary turn of its channel, and the drop is logged.
    [Fact]
    public async Task AnAppTurnFromAnotherChannel_IsDroppedAndLogged_AndTheTurnRunsAsAnyOther()
    {
        var run = new Run();
        run.Chat.WriteUnsigned(Ask(ChannelIds.SignalR) with { Assertion = TestAssertions.Of(_viewer, ChannelIds.SignalR) });

        await run.MonitorAsync();

        run.Agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem().IsAppTurn().ShouldBeFalse();
        run.Logger.Entries.ShouldContain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("app turn"));
    }

    // A household's own server that happens to be named `apps` is not the deployment's apps server: what it
    // sends is an ordinary turn of its channel.
    [Fact]
    public async Task AnAppTurnFromAHouseholdsOwnAppsServer_IsDropped()
    {
        var own = new FakeChannelConnection { ChannelId = AppChannels.Apps, Household = ChannelHousehold.Own(Household, false) };
        var run = new Run(apps: own);
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        run.Agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem().IsAppTurn().ShouldBeFalse();
    }

    [Fact]
    public async Task AMessageOnTheAppsChannel_WithNoAppTurn_IsRefusedAndAnswered()
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask() with { AppTurn = null });

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.NotVerified);
        run.Errors.ShouldHaveSingleItem().Message.ShouldNotBeNull().ShouldContain(TurnAdmission.NotAnAppTurn);
    }

    public static TheoryData<string, string?> Unbelieved => new()
    {
        { "no assertion", null },
        { "another channel's", TestAssertions.Of(_viewer, ChannelIds.SignalR) },
        { "the guest's", TestAssertions.OfGuest(Household, AppChannels.Apps) },
        { "nobody's", TestAssertions.OfNobody(AppChannels.Apps) }
    };

    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task AnAsk_WhoseAssertionIsNotAPersonsForApps_IsRefusedAndAnswered(string _, string? assertion)
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask() with { Assertion = assertion });

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.NotVerified);
        run.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.AssertionRefusedErrorType);
    }

    [Fact]
    public async Task AnAsk_OfASuspendedHousehold_IsRefusedAndAnswered()
    {
        var run = new Run(households: Households(suspended: true));
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.Suspended);
    }

    [Fact]
    public async Task AnAsk_OfADisabledMember_IsRefusedAndAnswered()
    {
        var run = new Run(standing: new FakeAccountStanding(_viewer));
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.Disabled);
    }

    [Fact]
    public async Task AnAsk_OfAHouseholdWithoutTheAppsFeature_IsRefusedAndAnswered()
    {
        var run = new Run(households: Households(apps: false));
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.AppsOff);
        RefusalAnswers.CodeOf(RefusalAnswers.AppsOff).ShouldBe("apps-off");
        run.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.FeatureOffErrorType);
    }

    // Never answered by another agent: the ask names the agent that built the app, and one the household no
    // longer has refuses it by name — a person's own registered agent of that id is not the household's.
    [Fact]
    public async Task AnAsk_NamingABuildingAgentTheHouseholdNoLongerHas_IsRefusedByName()
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask() with { AgentId = "gone" });

        await run.MonitorAsync();

        run.ShouldHaveRefused(RefusalAnswers.AgentGone("gone"));
        RefusalAnswers.AgentGone("gone").ShouldContain("'gone'");
        RefusalAnswers.CodeOf(RefusalAnswers.AgentGone("gone")).ShouldBe("agent-gone");
        run.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.AgentNotFoundErrorType);
    }

    // The factory is the last word on whether the agent can be built for this turn: refusing it there too,
    // the ask is answered by name rather than left to the apps server's timeout.
    [Fact]
    public async Task AnAsk_WhoseAgentTheFactoryCannotBuild_IsAnsweredByName()
    {
        var run = new Run();
        run.Factory.Refusal = new AgentNotFoundException("jonas", Household);
        run.Apps.WriteUnsigned(Ask());

        await run.MonitorAsync();

        run.Agent.ReceivedMessages.ShouldBeEmpty();
        run.Apps.SentReplies.Select(r => (r.ContentType, r.Content, r.TurnKey))
            .ShouldBe([(ReplyContentType.Error, RefusalAnswers.AgentGone("jonas"), "turn-1"), (ReplyContentType.StreamComplete, "", "turn-1")]);
    }

    // The apps channel's default-agent fallback is never used: an ask that names no agent is not routed to
    // the household's default, and is refused as one naming an agent it does not have.
    [Fact]
    public async Task AnAsk_NamingNoAgent_IsNotRoutedToTheHouseholdsDefault()
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask() with { AgentId = null });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Apps.SentReplies.First().ContentType.ShouldBe(ReplyContentType.Error);
    }

    // An app's conversations never show in WebChat: whatever the message asks for, no review copy is made
    // and no conversation is minted on any other channel.
    [Fact]
    public async Task AnAppTurn_MintsNoTopic_EvenWhenItAsksForAReviewCopyOrAnotherTarget()
    {
        var run = new Run();
        run.Apps.WriteUnsigned(Ask() with
        {
            ReviewCopy = new ReviewCopy { Title = "Ask" },
            ReplyTo = [new ReplyTarget(ChannelIds.SignalR, null)]
        });

        await run.MonitorAsync();

        run.Agent.ReceivedMessages.ShouldHaveSingleItem();
        run.Chat.CreatedConversations.ShouldBeEmpty();
        var context = run.Agent.ReceivedMessages.Single().Single().GetConversationContext().ShouldNotBeNull();
        context.Origin.ChannelId.ShouldBe(AppChannels.Apps);
        context.ConversationId.ShouldBe("ask-1");
    }

    private static ChannelMessage Ask(string channel = AppChannels.Apps) => new()
    {
        ConversationId = "ask-1",
        Content = "How many carbs in a slice of bread?",
        Sender = "app",
        ChannelId = channel,
        AgentId = "jonas",
        TurnKey = "turn-1",
        Principal = new Principal(channel, _viewer.UserId, _viewer),
        Assertion = TestAssertions.Of(_viewer, channel),
        AppTurn = _app
    };

    // The household as the agent reads it: Jonas its agent, built apps on unless a test says, running
    // unless a test suspends it.
    private static IAgentDefinitionProvider Households(bool apps = true, bool suspended = false)
    {
        var provider = new Mock<IAgentDefinitionProvider>();
        provider
            .Setup(p => p.ForHouseholdAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? household, CancellationToken _) => new HouseholdAgents
            {
                HouseholdId = household,
                Agents = [new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "test/model", McpServerEndpoints = [] }],
                Configuration = new AgentHouseholdConfig(
                    household ?? "home", false, new HouseholdSettings { Name = "Casa", TimeZone = "UTC" },
                    new AgentsSection(), new FeaturesSection { Apps = apps }, new SpacesSection(), "key",
                    Suspended: suspended)
            });
        return provider.Object;
    }

    private sealed class Run(
        FakeChannelConnection? apps = null, IAgentDefinitionProvider? households = null, IAccountStanding? standing = null)
    {
        private readonly List<MetricEvent> _published = [];

        public FakeChannelConnection Apps { get; } = apps ?? new FakeChannelConnection { ChannelId = AppChannels.Apps };

        // The review host, which an app turn must never reach.
        public FakeChannelConnection Chat { get; } =
            new() { ChannelId = ChannelIds.SignalR, HostsReviewCopies = true, ConversationIdToReturn = "topic-1" };

        public FakeAiAgent Agent { get; } = new();

        public FakeAgentFactory Factory => _factory ??= new FakeAgentFactory(Agent);

        private FakeAgentFactory? _factory;

        public RecordingLogger<ChatMonitor> Logger { get; } = new();

        public IReadOnlyList<ErrorEvent> Errors
        {
            get
            {
                lock (_published)
                {
                    return [.. _published.OfType<ErrorEvent>()];
                }
            }
        }

        public async Task MonitorAsync()
        {
            Apps.Complete();
            Chat.Complete();
            var metrics = new Mock<IMetricsPublisher>();
            metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
                .Callback<MetricEvent>(e =>
                {
                    lock (_published)
                    {
                        _published.Add(e);
                    }
                });
            var monitor = new ChatMonitor(
                [Apps, Chat], Factory, MonitorTestMocks.CreateThreadResolver(), metrics.Object, null, Logger,
                MonitorTestMocks.Assertions, standing: standing ?? new FakeAccountStanding(),
                agentDefinitions: households ?? Households());

            await monitor.Monitor(CancellationToken.None);
        }

        public void ShouldHaveRefused(string answer)
        {
            Agent.ReceivedMessages.ShouldBeEmpty();
            Factory.Created.ShouldBeEmpty();
            Apps.SentReplies.Select(r => (r.ContentType, r.Content, r.TurnKey))
                .ShouldBe([(ReplyContentType.Error, answer, "turn-1"), (ReplyContentType.StreamComplete, "", "turn-1")]);
        }
    }
}