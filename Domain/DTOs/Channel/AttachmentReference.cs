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
}