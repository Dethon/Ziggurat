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
    string? ResponseFormat = null,

    // The message's name in its conversation (HistoryEntries), what a page rewrites or forks from.
    string? EntryId = null,

    // The message begins a place the conversation branched: which version of how many is shown, and the
    // slot to switch it by.
    MessageVersions? Versions = null,

    // Not a message: the line a slot with nothing shown is drawn as (a reroll stopped or failed), which
    // carries the slot's versions.
    bool NoReply = false);

public sealed record MessageVersions(string Slot, int Index, int Count);