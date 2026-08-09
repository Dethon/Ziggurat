using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// Attachments are projected alongside the text, and a message that carried only files has to
// survive the read: the transcript is a record of what was sent, not of what was typed.
[UsedImplicitly]
public record ChatHistoryMessage(
    string? MessageId,
    string Role,
    string Content,
    string? SenderId,
    DateTimeOffset? Timestamp,
    IReadOnlyList<AttachmentReference>? Attachments = null);