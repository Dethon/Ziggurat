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

    // The same three limits as the wire DTO the browser asks for, so the endpoint and the
    // composer refuse from one set of numbers.
    public AttachmentLimits Limits => new(MaxBytesPerFile, MaxFilesPerMessage, AllowedMediaTypes);
}