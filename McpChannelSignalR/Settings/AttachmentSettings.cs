using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Settings;

// What the operator gets to tune about attachments. All of it is generic and lives in
// appsettings.json — including StoragePath, which is where the store sits *inside* the container
// and is the same everywhere. What varies per deployment is the volume mounted there, which is
// the compose file's business rather than a setting.
public record AttachmentSettings
{
    public string StoragePath { get; init; } = "/data/uploads";

    public long MaxBytesPerFile { get; init; } = 25L * 1024 * 1024;

    public int MaxFilesPerMessage { get; init; } = 10;

    // Short on purpose: a ticket only has to survive the picking of one message's files.
    public int TicketTtlSeconds { get; init; } = 900;

    public IReadOnlyList<string> AllowedMediaTypes { get; init; } =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "application/pdf"
    ];

    // Kinds kept only as a review copy, never taken from a person (ADR 0046): the clip a channel
    // sent the model, so the review shows what a video model saw. Only what a channel sends after
    // normalising — Sugary's H.264 MP4 — so nothing rests here that a browser might fail to play.
    public IReadOnlyList<string> ReviewCopyMediaTypes { get; init; } = ["video/mp4"];

    // A clip reaches the store as one store_attachment call, its bytes base64 in the body, and
    // Kestrel caps a request body at 30,000,000 bytes. 20 MiB is 27,962,028 bytes of base64, which
    // leaves the envelope room; a 20 s clip normalised to 540 lines runs a few MB.
    public long MaxReviewCopyClipBytes { get; init; } = 20L * 1024 * 1024;

    // The same three limits as the wire DTO the browser asks for, so the endpoint and the
    // composer refuse from one set of numbers.
    public AttachmentLimits Limits => new(MaxBytesPerFile, MaxFilesPerMessage, AllowedMediaTypes);
}