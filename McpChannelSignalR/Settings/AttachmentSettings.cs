namespace McpChannelSignalR.Settings;

// What the operator gets to tune about attachments. Everything here is a generic knob and lives
// in appsettings.json; only StoragePath is per-deployment, because it names a volume.
public record AttachmentSettings
{
    public string StoragePath { get; init; } = "/data/uploads";

    public long MaxBytesPerFile { get; init; } = 25L * 1024 * 1024;

    public int MaxFilesPerMessage { get; init; } = 10;

    // Short on purpose: a ticket only has to survive the picking of one message's files.
    public int TicketTtlSeconds { get; init; } = 900;

    // Matches the window the conversation history already keeps, so a reference and the file it
    // names disappear on the same order of timescale.
    public int RetentionDays { get; init; } = 30;

    public IReadOnlyList<string> AllowedMediaTypes { get; init; } =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "application/pdf"
    ];
}