using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// A channel sending on a person's behalf can ask for the conversation to be shown on the review
// host as well. It is the fan-out agent-initiated messages already have, with the sending channel
// as the first target: the conversation, the history, the approvals and the context all name the
// channel's own conversation, and the review host attaches to that same id on every turn.
public class ChatMonitorReviewCopyTests
{
    private const string Conversation = "11:22";
    private static readonly ReviewCopy _review = new() { Title = "Meal estimate" };

    [Fact]
    public async Task AChannelTurnAskingForAReviewCopy_AttachesTheReviewHostToItsConversation_WithTheChannelFirst()
    {
        var message = Turn("a plate of paella") with { ReviewCopy = _review };
        var sugary = MonitorTestMocks.CreateChannel("sugary", message);
        var webchat = ReviewHost();
        var agent = ReplyingAgent();
        var factory = MonitorTestMocks.CreateAgentFactory(agent);

        await RunAsync([sugary, webchat], factory);

        var asked = webchat.CreatedConversations.ShouldHaveSingleItem();
        asked.AgentId.ShouldBe("sugary");
        asked.TopicName.ShouldBe("Meal estimate");
        asked.Sender.ShouldBe("sugary-app");
        asked.InitialPrompt.ShouldBe("a plate of paella");
        asked.ExistingConversationId.ShouldBe(Conversation);

        // The channel is the first target: the agent is built on its conversation, approvals go to
        // it, and the turn's context names it as the conversation the answer belongs to.
        var created = factory.Created.ShouldHaveSingleItem();
        created.Key.ShouldBe(new AgentKey(Conversation, "sugary"));
        created.ApprovalHandler.ShouldBeSameAs(sugary);
        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.ShouldHaveSingleItem().GetConversationContext()!.Origin
            .ShouldBe(new ReplyTarget("sugary", Conversation));

        // Both are sent the reply as it was written, on the one conversation id.
        sugary.SentReplies.ShouldContain(r => r.Content == "{\"items\":[]}" && r.ConversationId == Conversation);
        webchat.SentReplies.ShouldContain(r => r.Content == "{\"items\":[]}" && r.ConversationId == Conversation);
    }

    [Fact]
    public async Task EveryLaterTurnAskingForAReviewCopy_AttachesToTheSameConversationAgain()
    {
        var first = Turn("a plate of paella") with { ReviewCopy = _review };
        var answer = Turn("about 300 g") with { ReviewCopy = _review };
        var sugary = MonitorTestMocks.CreateChannel("sugary", first, answer);
        var webchat = ReviewHost();

        await RunAsync([sugary, webchat], MonitorTestMocks.CreateAgentFactory(ReplyingAgent()));

        webchat.CreatedConversations.Select(c => (c.InitialPrompt, c.ExistingConversationId))
            .ShouldBe([("a plate of paella", Conversation), ("about 300 g", Conversation)]);
        webchat.SentReplies.Where(r => r.ContentType == ReplyContentType.StreamComplete).Count().ShouldBe(2);
    }

    [Fact]
    public async Task ATurnNotAskingForAReviewCopy_GetsNone()
    {
        var sugary = MonitorTestMocks.CreateChannel("sugary", Turn("a plate of paella"));
        var webchat = ReviewHost();

        await RunAsync([sugary, webchat], MonitorTestMocks.CreateAgentFactory(ReplyingAgent()));

        webchat.CreatedConversations.ShouldBeEmpty();
        webchat.SentReplies.ShouldBeEmpty();
        sugary.SentReplies.ShouldContain(r => r.Content == "{\"items\":[]}");
    }

    private static ChannelMessage Turn(string content) => new()
    {
        ConversationId = Conversation,
        Content = content,
        Sender = "sugary-app",
        ChannelId = "sugary",
        AgentId = "sugary"
    };

    private static FakeChannelConnection ReviewHost()
    {
        var webchat = new FakeChannelConnection
        {
            ChannelId = "signalr",
            HostsReviewCopies = true,
            ConversationIdToReturn = Conversation
        };
        webchat.Complete();
        return webchat;
    }

    private static FakeAiAgent ReplyingAgent() => new()
    {
        UpdatesToYield = [new AgentResponseUpdate { Contents = [new TextContent("{\"items\":[]}")] }]
    };

    private static Task RunAsync(IReadOnlyList<IChannelConnection> channels, FakeAgentFactory factory) =>
        new ChatMonitor(
                channels,
                factory,
                MonitorTestMocks.CreateThreadResolver(),
                new Mock<IMetricsPublisher>().Object,
                null,
                new Mock<ILogger<ChatMonitor>>().Object)
            .Monitor(CancellationToken.None);
}