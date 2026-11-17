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

    // A queued message whose turn started before the person removed or took it back.
    public const string AlreadySent = "This message was already sent.";

    // A queued message a stop handed back before the person removed or took it back: it is not lost and
    // was not sent, and it is in their message box, or will be when they next open the conversation.
    public const string ReturnedToComposer = "The reply was stopped, so this message went back to your message box.";

    // A queued message is withdrawn or steered with by the person who sent it alone, whoever else sees the queue.
    public const string NotTheSenders = "Only the person who sent this message can remove, edit or steer with it.";

    // Steer now cuts the running turn short, so only the person that turn is for may: never another
    // member's turn, nor a turn an agent started for a schedule or a watch.
    public const string NotYourTurn = "Only a reply to your own message can be steered. This message waits its turn.";

    // Nothing answered the turn's emit. The message may still sit in a buffer for an agent that comes
    // back, so this says the turn cannot be answered now rather than claiming it was thrown away.
    public const string NotLive = "No agent is connected right now, so this message has not been answered.";

    // The accounts service could not vouch for the person just now, so no turn could carry its word;
    // nothing was sent, and sending again later may work.
    public const string Unvouched = "Who you are could not be checked right now, so this message has not been answered.";

    // The session the page signed in with is no longer live (revoked from the account page, or a
    // connection that came with none to ask about): nothing is sent until it signs in again, and
    // sending again will not help.
    public const string SignedOut = "This device was signed out, so this message has not been answered. Sign in again to keep chatting.";

    // A message naming a file kept for another conversation: a page names files by id, and an id is no
    // proof the sender holds the file here.
    public const string NotYourFiles = "This message names a file that was not added to this conversation, so it was not sent.";

    // A queued message whose sending failed after it was taken from the queue: said so its turn ends
    // rather than hold the stream open for good.
    public const string NotSent = "This message could not be sent.";
}