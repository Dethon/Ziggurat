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

public record UserMessageNotification(
    string TopicId,
    string Content,
    string? SenderId,
    DateTimeOffset? Timestamp,
    string? CorrelationId = null,
    string? SpaceSlug = null);