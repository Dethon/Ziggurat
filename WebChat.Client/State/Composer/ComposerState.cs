using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Composer;

public enum AttachmentStatus
{
    Uploading,
    Ready,
    Failed
}

// One file the person attached, from the moment they picked it. The local id is the browser's
// own handle on it: it exists before the upload store has any name for the file, which is what
// lets progress, cancel and remove address a file that has not landed yet.
public sealed record ComposerAttachment
{
    public required string LocalId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required long SizeBytes { get; init; }
    public AttachmentStatus Status { get; init; } = AttachmentStatus.Uploading;
    public int PercentComplete { get; init; }
    public AttachmentReference? Reference { get; init; }
    public string? Error { get; init; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed record ComposerState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ComposerAttachment>> AttachmentsByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ComposerAttachment>>();

    // What the server will accept, asked once and used to refuse a file as it is picked rather
    // than after it uploads. Null until the connection has answered; the server refuses the same
    // cases anyway, so a pick before then is a slower refusal rather than a wrong one.
    public AttachmentLimits? Limits { get; init; }

    public IReadOnlyList<ComposerAttachment> For(string? topicId) =>
        topicId is null ? [] : AttachmentsByTopic.GetValueOrDefault(topicId, []);

    public static ComposerState Initial => new();
}