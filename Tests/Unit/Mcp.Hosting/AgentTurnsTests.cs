using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// A channel server that waits on the agent sends a turn under a key of its own and is handed the
// whole reply to that key alone: the last message's text, or why there is none.
public sealed class AgentTurnsTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private readonly ChannelInboxProbe _agent = new("test", DeliveryPolicy.GateOnLive);
    private readonly AgentTurns _turns;

    public AgentTurnsTests() => _turns = new AgentTurns(_agent.Emitter, NullLogger<AgentTurns>.Instance);

    [Fact]
    public async Task ATurn_IsSentUnderAKeyOfItsOwn_AndAnsweredByItsLastMessage()
    {
        var reply = Send();
        var turnKey = (await _agent.FirstAsync(_wait)).TurnKey!;

        Reply(turnKey, "messageA", "Let me look.");
        Reply("another-turn", "messageX", "Not this one.");
        Reply(turnKey, "messageB", "The ");
        Reply(turnKey, "messageB", "answer.");
        Reply(turnKey, null, "", ReplyContentType.StreamComplete, isComplete: true);

        (await reply.WaitAsync(_wait)).ShouldBe(new TurnReply.Answered("The answer."));
    }

    [Fact]
    public async Task AnError_IsTheTurnFailing()
    {
        var reply = Send();
        var turnKey = (await _agent.FirstAsync(_wait)).TurnKey!;

        Reply(turnKey, null, "refused", ReplyContentType.Error);
        Reply(turnKey, null, "", ReplyContentType.StreamComplete, isComplete: true);

        (await reply.WaitAsync(_wait)).ShouldBe(new TurnReply.Failed("refused"));
    }

    [Fact]
    public async Task ATurnThatEndsWithNoText_IsUnanswered()
    {
        var reply = Send();
        var turnKey = (await _agent.FirstAsync(_wait)).TurnKey!;

        Reply(turnKey, null, "", ReplyContentType.StreamComplete, isComplete: true);

        (await reply.WaitAsync(_wait)).ShouldBe(new TurnReply.Unanswered());
    }

    [Fact]
    public async Task ATurnNobodyPollsFor_IsNotListening()
    {
        var nobody = new ChannelInboxProbe("test", DeliveryPolicy.GateOnLive, live: false);
        var turns = new AgentTurns(nobody.Emitter, NullLogger<AgentTurns>.Instance);

        (await turns.SendAsync(Message(), _wait, TimeProvider.System, CancellationToken.None))
            .ShouldBe(new TurnReply.NotListening());
    }

    private Task<TurnReply> Send() => _turns.SendAsync(Message(), _wait, TimeProvider.System, CancellationToken.None);

    private static ChannelMessageNotification Message() => new()
    {
        ConversationId = "conversation", Sender = "someone", Content = "a question", Timestamp = DateTimeOffset.UnixEpoch
    };

    private void Reply(
        string turnKey, string? messageId, string content, ReplyContentType type = ReplyContentType.Text,
        bool isComplete = false) =>
        _turns.Receive(new SendReplyParams
        {
            ConversationId = "conversation",
            Content = content,
            ContentType = type,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turnKey
        });
}