using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// What the chat hub answers about a topic, which the client also reads: worded once so the two agree.
[PublicAPI]
public static class TopicRefusals
{
    // The one answer for a topic the caller may not see, worded as for one that does not exist, so
    // naming another person's private topic tells the namer nothing about it — not even that it is.
    public const string NoSuchConversation = "No such conversation.";

    // A topic another channel goes on showing (TopicMirrors) is forked, never rewritten.
    public const string Mirrored = "This conversation is also shown on another channel, so it can't be changed here. Fork it to change it.";

    // The page acted on a conversation that moved on since it was drawn.
    public const string Changed = "This conversation changed since it was shown. Look again and retry.";

    // Only a person's own message is sent again: not a reply, not a turn a service started.
    public const string NotRerunnable = "Only a message somebody wrote can be run again.";

    // Continue carries on a reply that was cut off, and the last one ended.
    public const string NothingToContinue = "The last reply finished, so there is nothing to continue.";

    // A turn is writing to the conversation, and would write over the change.
    public const string ReplyRunning = "A reply is still being written. Wait for it to finish, or stop it.";
}