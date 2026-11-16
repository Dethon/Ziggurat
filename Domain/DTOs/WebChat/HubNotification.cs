using Domain.DTOs.Channel;
using Domain.DTOs.Voice;

namespace Domain.DTOs.WebChat;

public enum TopicChangeType
{
    Created,
    Updated,
    Deleted
}

public record TopicChangedNotification(
    TopicChangeType ChangeType,
    string TopicId,
    TopicMetadata? Topic = null,
    string? SpaceSlug = null);

// Starting is the only thing a server in this repo pushes about a stream, so the push says
// exactly that instead of carrying a change type with one value. A stream ending is a
// client-side fact: the chunk loop finishing, the stop button, or a topic being deleted.
public record StreamStartedNotification(
    string TopicId,
    string? SpaceSlug = null);

// A fork: the new topic, and the words of the message it was forked from, handed back to the composer.
public record ForkedTopic(TopicMetadata Topic, string? ComposerText);

// A person rewrote the conversation — deleted, edited, rerolled, switched a version — so every page on
// it reads its history again.
public record HistoryChangedNotification(
    string TopicId,
    string? SpaceSlug = null);

// Taking the prompt off every browser showing it is the whole of this push. What an approval let
// through is written into the topic's stream, which is the one route a tool call travels by.
// AnsweredElsewhere: the question was answered on another of its asker's channels, so a card still
// showing it says so instead of vanishing. TopicId is empty for a question offered to a person
// rather than asked in a topic.
public record ApprovalResolvedNotification(
    string TopicId,
    string ApprovalId,
    string? SpaceSlug = null,
    bool AnsweredElsewhere = false);

// A question another channel asked — nabu aloud, Telegram — offered to its asker on every chat
// client they have open, wherever they are. TopicId is the topic over the conversation it is about,
// when this server holds one.
public record ApprovalOfferedNotification(
    ToolApprovalRequestMessage Request,
    string? TopicId = null);

// A timer ringing for this person — one with no satellite to ring on — shown insistently on every
// chat client they have open until they dismiss it on one of them, or it ends elsewhere.
public record AlertOfferedNotification(
    string AlertId,
    string Text,
    AnnounceKind Kind = AnnounceKind.Timer);

// The alert ended — dismissed here or on another of the person's channels, or rung out — so every
// client showing it takes it down.
public record AlertSettledNotification(
    string AlertId,
    bool Dismissed);

public record UserMessageNotification(
    string TopicId,
    string Content,
    string? SenderId,
    DateTimeOffset? Timestamp,
    string? CorrelationId = null,
    string? SpaceSlug = null);

// The agent registered its agents again. It carries no list: every household is shown its own agents,
// so a client asks for its own (ChatHub.GetAgents) rather than being sent the home's.
public record AgentsUpdatedNotification;

// The household's people changed (a role, "sees all conversations", a member disabled): what this
// connection may see was read off its assertion as it opened, so the page opens a fresh one.
public record AccessChangedNotification;
// A person's message waiting for the running turn to end, as every page on the topic shows it: whose
// it is, so a page labels other people's and offers its own person theirs, and what it carries.
public record QueuedMessageItem(
    string Id,
    string Content,
    int FileCount,
    string SenderId,
    string SenderName,
    DateTimeOffset QueuedAt);

// A queued message handed back to its sender — taken back to be edited — as they queued it: its words,
// its files as the upload store holds them, and the model and effort it was sent with.
public record ReturnedMessage(
    string Id,
    string Content,
    IReadOnlyList<AttachmentReference> Attachments,
    AgentConfigPatch? ConfigPatch);

// A topic's queue changed. The whole queue, in the order it will run, so a page replaces what it
// showed; Revision orders two pushes sent close together, since they may arrive the other way round.
public record QueueChangedNotification(
    string TopicId,
    IReadOnlyList<QueuedMessageItem> Items,
    long Revision,
    string? SpaceSlug = null);
// A stop handed back messages this person queued on a topic: a page showing it takes them
// (ChatHub.TakeReturned) into its composer; any other finds them when the topic is next opened.
public record QueueReturnedNotification(string TopicId, string? SpaceSlug = null);