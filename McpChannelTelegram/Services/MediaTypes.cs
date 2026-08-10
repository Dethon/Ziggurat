using Domain.DTOs.Channel;

namespace McpChannelTelegram.Services;

// Mime type first, filename extension second. Telegram carries no type at all for a photo and
// some clients describe a PDF as a generic blob, so a file must not be refused over a technicality
// the extension already settles.
internal static class MediaTypes
{
    private const string BinaryExtension = ".bin";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif"
    };

    private static readonly Dictionary<string, string> ExtensionByMediaType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/bmp"] = ".bmp",
            ["image/tiff"] = ".tif",
            ["image/heic"] = ".heic",
            ["image/heif"] = ".heif"
        };

    // The extension gets its say whenever the declared type resolves to no kind, which covers both
    // a missing type and a generic one without having to keep a list of what "generic" means.
    public static string? Resolve(TelegramMedia media) =>
        AttachmentKinds.ForMediaType(media.MimeType) is not null
            ? media.MimeType
            : FromExtension(media.FileName) ?? media.MimeType;

    // A document keeps the name Telegram carried. Media that has none — a photo, a sticker — is
    // named after the message it arrived in: the extension is load-bearing once the file lands in
    // a sandbox, and the message id keeps two unnamed items of one album apart without dragging in
    // the reference id, which is long.
    public static string NameFor(TelegramMedia media, int messageId, string? mediaType) =>
        string.IsNullOrWhiteSpace(media.FileName)
            ? $"attachment-{messageId}{ExtensionFor(mediaType)}"
            : media.FileName;

    private static string? FromExtension(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? null
            : ByExtension.GetValueOrDefault(Path.GetExtension(fileName));

    private static string ExtensionFor(string? mediaType) =>
        mediaType is null ? BinaryExtension : ExtensionByMediaType.GetValueOrDefault(mediaType, BinaryExtension);
}