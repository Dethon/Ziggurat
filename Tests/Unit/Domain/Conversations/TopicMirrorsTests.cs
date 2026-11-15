using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

// A topic over another channel's conversation shares its history with a channel that goes on showing
// what it showed, so it is forked, never rewritten.
public class TopicMirrorsTests
{
    [Fact]
    public void ATopicMarkedAsAMirror_IsOne()
    {
        var topic = Topic("t") with { Mirrored = true };

        TopicMirrors.IsMirrored(topic, []).ShouldBeTrue();
    }

    [Fact]
    public void AChatClientTopicOverItsOwnConversation_IsNot()
    {
        TopicMirrors.IsMirrored(Topic("t"), [new ChatMessage(ChatRole.User, "hola")]).ShouldBeFalse();
    }

    // A review copy is a topic over a conversation its id does not name.
    [Fact]
    public void ATopicOverAConversationItsIdDoesNotName_IsAReviewCopy()
    {
        var topic = Topic("t") with { ChatId = 42, ThreadId = 42 };

        TopicMirrors.IsMirrored(topic, []).ShouldBeTrue();
    }

    // A voice topic from before the mark is told by where its first message came from.
    [Fact]
    public void ATopicWhoseFirstMessageCameFromVoice_IsAVoiceConversation()
    {
        var message = new ChatMessage(ChatRole.User, "enciende la luz");
        message.SetConversationContext(new ConversationContext("nabu", "1:2", "fran", new ReplyTarget(ChannelIds.Voice, "1:2")));

        TopicMirrors.IsMirrored(Topic("t"), [message]).ShouldBeTrue();
    }

    private static TopicMetadata Topic(string topicId)
    {
        var id = ConversationIdGenerator.CreateFor(topicId);
        return new TopicMetadata(topicId, id.ChatId, id.ThreadId, "jonas", "A topic", DateTimeOffset.UtcNow, null);
    }
}