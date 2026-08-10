using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// One piece of media as this channel needs to see it, lifted off whichever field of a Telegram
// message it arrived on. A message carries at most one, so this is a `Message -> media?` function
// and not a list.
internal sealed record TelegramMedia(string FileId, string? FileName, string? MimeType, long? SizeBytes)
{
    // Order matters where Telegram sets two fields for backward compatibility: an animation also
    // sets Document and a live photo also sets Photo, so the more specific field is asked first.
    public static TelegramMedia? Read(Message message) => message switch
    {
        // Telegram has no mime type for a sticker; the format follows from the two flags. Static
        // stickers are WebP, which the shared kind mapping already reads as an image.
        { Sticker: { } sticker } => new TelegramMedia(
            sticker.FileId,
            null,
            sticker switch
            {
                { IsVideo: true } => "video/webm",
                { IsAnimated: true } => "application/x-tgsticker",
                _ => "image/webp"
            },
            sticker.FileSize),
        { Animation: { } animation } => new TelegramMedia(
            animation.FileId, animation.FileName, animation.MimeType, animation.FileSize),
        { VideoNote: { } note } => new TelegramMedia(note.FileId, null, null, note.FileSize),
        { Video: { } video } => new TelegramMedia(
            video.FileId, video.FileName, video.MimeType, video.FileSize),
        { Voice: { } voice } => new TelegramMedia(voice.FileId, null, voice.MimeType, voice.FileSize),
        { Audio: { } audio } => new TelegramMedia(
            audio.FileId, audio.FileName, audio.MimeType, audio.FileSize),
        { Document: { } document } => new TelegramMedia(
            document.FileId, document.FileName, document.MimeType, document.FileSize),
        // A photo arrives as the same picture in several sizes; the largest is the one worth
        // sending. Telegram re-encodes every photo to JPEG and carries neither a mime type nor a
        // filename for one, so the type is a fact about the transport rather than a guess.
        { Photo: { Length: > 0 } sizes } => LargestPhoto(sizes),
        _ => null
    };

    private static TelegramMedia LargestPhoto(IReadOnlyList<PhotoSize> sizes)
    {
        var largest = sizes.MaxBy(size => size.FileSize ?? (long)size.Width * size.Height)!;
        return new TelegramMedia(largest.FileId, null, "image/jpeg", largest.FileSize);
    }
}