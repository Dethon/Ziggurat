using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// What the chat hub answers about a topic, which the client also reads: worded once so the two agree.
[PublicAPI]
public static class TopicRefusals
{
    // The one answer for a topic the caller may not see, worded as for one that does not exist, so
    // naming another person's private topic tells the namer nothing about it — not even that it is.
    public const string NoSuchConversation = "No such conversation.";
}