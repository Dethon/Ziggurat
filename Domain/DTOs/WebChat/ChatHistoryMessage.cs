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
    IReadOnlyList<AttachmentReference>? Attachments = null,

    // The name the message was sent under, beside SenderId's account: how somebody else reading a
    // shared topic is shown who wrote it.
    string? SenderName = null,

    // What a person was told instead of an answer, when their turn failed: drawn as the error it
    // was, with its retry.
    bool IsError = false,

    // The structured answer its turn asked for (ResponseFormat.Name), on the request an app composed and
    // on the answer it got: how the chat tells an app's request from a typed one, and reads the answer.
    string? ResponseFormat = null);