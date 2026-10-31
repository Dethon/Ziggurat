using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

// One turn has one conversation id. A schedule fire carries a synthetic ConversationId
// ("sched-...") and a ReplyTo entry with a null ConversationId, prompting the target
// channel to mint a fresh WebChat conversation. Everything the turn produces — the agent
// it is built from, the chat history it restores, and the events it publishes — must name
// the minted conversation, because that is the one an operator can open.
public class ChatMonitorDeliveryIdentityTests
{
    private static readonly AgentKey _mintedKey = new("7:9", "jonas");

    private static readonly ToolApprovalRequest _anyRequest =
        new(null, "some_tool", new Dictionary<string, object?>());

    [Fact]
    public async Task Monitor_ScheduledMessageMintingConversation_BuildsTheWholeTurnFromTheMintedId()
    {
        var scheduleMessage = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "do the thing",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
            ReplyTo = [new ReplyTarget("webchat", null)]
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduleMessage);
        var webchat = new FakeChannelConnection
        {
            ChannelId = "webchat",
            ConversationIdToReturn = _mintedKey.ConversationId
        };
        webchat.Complete();
        var fakeAgent = ReplyingAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var published = new List<MetricEvent>();
        var recallHook = new RecordingRecallHook();

        var monitor = new ChatMonitor(
            [scheduling, webchat],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            recallHook,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        var created = agentFactory.Created.ShouldHaveSingleItem();
        created.Key.ShouldBe(_mintedKey);
        fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem().ShouldBe(_mintedKey.ToString());
        FirstReplyOf(published).ConversationId.ShouldBe(_mintedKey.ConversationId);
        // Recall provenance is durable: a memory extracted here records its source
        // conversation, and that has to be one somebody can still open months later.
        recallHook.ConversationIds.ShouldHaveSingleItem().ShouldBe(_mintedKey.ConversationId);

        // The approval route the turn was built with reaches the minted channel under the
        // minted id — the conversation the answer lands in, not the scheduling origin
        // (which auto-approves silently and would hide the tool calls from the user).
        await created.ApprovalHandler.NotifyAutoApprovedAsync(
            created.Key.ConversationId, [_anyRequest], CancellationToken.None);
        webchat.NotifyAutoApprovedCalls.ShouldHaveSingleItem()
            .ConversationId.ShouldBe(_mintedKey.ConversationId);
    }

    [Fact]
    public async Task Monitor_WebChatMessageWithoutReplyTo_BuildsTheWholeTurnFromItsOwnConversationId()
    {
        // The common path: no ReplyTo, so the delivery identity is the message's own
        // conversation id. This is the case that already works and must not regress.
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", message);
        var fakeAgent = ReplyingAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var published = new List<MetricEvent>();
        var recallHook = new RecordingRecallHook();

        var monitor = new ChatMonitor(
            [webchat],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            recallHook,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        var ownKey = new AgentKey("42:13", "jonas");
        agentFactory.Created.ShouldHaveSingleItem().Key.ShouldBe(ownKey);
        fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem().ShouldBe(ownKey.ToString());
        FirstReplyOf(published).ConversationId.ShouldBe(ownKey.ConversationId);
        recallHook.ConversationIds.ShouldHaveSingleItem().ShouldBe(ownKey.ConversationId);
    }

    [Fact]
    public async Task Monitor_LaterTurnResolvingItsOwnTargets_AttributesFirstReplyToItsOwnConversation()
    {
        // The group's delivery key names the conversation the FIRST turn's reply landed in —
        // here the minted WebChat one. A later plain message joins the group under the group
        // key and is delivered back to its own origin, so its first-reply latency belongs to
        // the conversation its own reply landed in, not to the group's delivery key.
        var fire = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "do the thing",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
            ReplyTo = [new ReplyTarget("webchat", null)]
        };
        var followUp = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "and again",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas"
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", fire, followUp);
        var webchat = new FakeChannelConnection
        {
            ChannelId = "webchat",
            ConversationIdToReturn = _mintedKey.ConversationId
        };
        webchat.Complete();
        var published = new List<MetricEvent>();

        var monitor = new ChatMonitor(
            [scheduling, webchat],
            MonitorTestMocks.CreateAgentFactory(ReplyingAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        var firstReplies = published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .Select(e => e.ConversationId)
            .ToList();
        firstReplies.ShouldBe([_mintedKey.ConversationId, "sched-morning-news-12345"]);
    }

    // A reply says nothing about which turn it answers unless the turn key is on it. Everything a
    // turn produces carries one key, minted here when the inbound message brought none, so the
    // receiving channel can compare rather than infer.
    [Fact]
    public async Task Monitor_AMessageWithNoTurnKey_GivesEveryReplyOfThatTurnOneMintedKey()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", message);

        await RunAsync([webchat]);

        // The stream-complete event included: that is exactly where today's message id goes null,
        // and it is the event that settles a turn.
        webchat.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
        var keys = webchat.SentReplies.Select(r => r.TurnKey).ToList();
        keys.ShouldAllBe(key => key != null);
        keys.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Monitor_AMessageCarryingATurnKey_EchoesThatKeyBackOnEveryReply()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "voice", agentId: "jonas") with
        {
            TurnKey = "turn-from-the-channel"
        };
        var voice = MonitorTestMocks.CreateChannel("voice", message);

        await RunAsync([voice]);

        voice.SentReplies.ShouldNotBeEmpty();
        voice.SentReplies.ShouldAllBe(r => r.TurnKey == "turn-from-the-channel");
    }

    [Fact]
    public async Task Monitor_TwoTurnsInOneConversation_CarryDifferentTurnKeys()
    {
        var first = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", content: "one", channelId: "webchat", agentId: "jonas");
        var second = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", content: "two", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", first, second);

        await RunAsync([webchat]);

        webchat.SentReplies.Select(r => r.TurnKey).Distinct().Count().ShouldBe(2);
    }

    // A timer or a schedule landing mid-conversation carries a key that does not match the live
    // turn's, and so does an abandoned answer arriving late. Without this flag the receiving end
    // cannot tell them apart, and the two have to be treated oppositely.
    [Fact]
    public async Task Monitor_AnAgentInitiatedTurn_SaysSoOnEveryReply()
    {
        var fire = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "do the thing",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
            ReplyTo = [new ReplyTarget("webchat", null)]
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", fire);
        var webchat = new FakeChannelConnection
        {
            ChannelId = "webchat",
            ConversationIdToReturn = _mintedKey.ConversationId
        };
        webchat.Complete();

        await RunAsync([scheduling, webchat]);

        webchat.SentReplies.ShouldNotBeEmpty();
        webchat.SentReplies.ShouldAllBe(r => r.AgentInitiated == true);
    }

    [Fact]
    public async Task Monitor_AUserTurn_SaysItWasNotAgentInitiated()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", message);

        await RunAsync([webchat]);

        webchat.SentReplies.ShouldNotBeEmpty();
        webchat.SentReplies.ShouldAllBe(r => r.AgentInitiated == false);
    }

    private static Task RunAsync(IReadOnlyList<IChannelConnection> channels) =>
        new ChatMonitor(
            channels,
            MonitorTestMocks.CreateAgentFactory(ReplyingAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher([]),
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier())
            .Monitor(CancellationToken.None);

    private sealed class RecordingRecallHook : IMemoryRecallHook
    {
        public List<string?> ConversationIds { get; } = [];

        public Task EnrichAsync(
            ChatMessage message,
            Account? account,
            string? conversationId,
            string? agentId,
            AgentSession thread,
            CancellationToken ct)
        {
            ConversationIds.Add(conversationId);
            return Task.CompletedTask;
        }
    }

    private static FakeAiAgent ReplyingAgent()
    {
        return new FakeAiAgent
        {
            UpdatesToYield = [new AgentResponseUpdate { Contents = [new TextContent("done")] }]
        };
    }

    private static IMetricsPublisher CapturingPublisher(List<MetricEvent> published)
    {
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });
        return metrics.Object;
    }

    private static LatencyEvent FirstReplyOf(List<MetricEvent> published)
    {
        return published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .ShouldHaveSingleItem();
    }
}