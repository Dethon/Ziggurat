using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// What stands in for an attachment everywhere the bytes are not wanted: where the file rests,
// what kind it is, what it is called and how large it is. It is what a conversation's history
// keeps, so reading a history costs the same whether or not files were ever sent.
//
// Deliberately transport-neutral: nothing here names SignalR, HTTP or the upload store's layout,
// so another channel can populate the same list without a redesign.
[PublicAPI]
public record AttachmentReference
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required long SizeBytes { get; init; }

    // On a clip: the ids of the still frames taken from it, stored as pictures of the same message.
    // The send picks the clip or these, never both, for the model the turn resolved to. Left off
    // the wire and out of the history when null, so every other reference reads as it always did.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Stills { get; init; }
}