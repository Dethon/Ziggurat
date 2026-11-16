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
}

// Where words handed to the composer go: after what is typed there (a fork's words, a send that went
// nowhere), or before it (a message given back from the queue, which was written first).
public enum TextPlacement
{
    After,
    Before
}

// Words handed to the composer from elsewhere, waiting to be put into it. Stamped as a transcript is,
// so the same words handed twice land twice.
public sealed record HandedWords(string Text, long Stamp, TextPlacement Placement);

public sealed record ComposerState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ComposerAttachment>> AttachmentsByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ComposerAttachment>>();

    // What the server will accept, asked once and used to refuse a file as it is picked rather
    // than after it uploads. Null until the connection has answered; the server refuses the same
    // cases anyway, so a pick before then is a slower refusal rather than a wrong one.
    public AttachmentLimits? Limits { get; init; }

    // One dictation at a time, and not per topic: the microphone is a property of the screen
    // rather than of a conversation, and leaving a topic ends whatever it was recording.
    public DictationState Dictation { get; init; } = new();

    // Apart from the dictation: these are not words the microphone heard, and going before what is typed
    // is theirs alone.
    public HandedWords? Handed { get; init; }

    public IReadOnlyList<ComposerAttachment> For(string? topicId) =>
        topicId is null ? [] : AttachmentsByTopic.GetValueOrDefault(topicId, []);

    public static ComposerState Initial => new();
}