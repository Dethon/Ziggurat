using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// Whether a piece of media was attached or performed, which is the whole of what decides if an
// unresolvable one is worth a complaint. Telegram says which field the media arrived on, so the
// split costs nothing to determine.
internal enum MediaIntent
{
    // A document, a video, an audio file: someone chose a file and sent it, so a refusal is news.
    Attached,

    // A sticker, an animation, a video note, a voice message: someone punctuated a conversation.
    // A complaint about unsupported file types here is noise.
    Performed
}

// One piece of media as this channel needs to see it, lifted off whichever field of a Telegram
// message it arrived on. A message carries at most one, so this is a `Message -> media?` function
// and not a list.
internal sealed record TelegramMedia(
    string FileId, string? FileName, string? MimeType, long? SizeBytes, MediaIntent Intent)
{
    // Order matters where Telegram sets two fields for backward compatibility: an animation also
    // sets Document and a live photo also sets Photo, so the more specific field is asked first.
    public static TelegramMedia? Read(Message message) => message switch
    {
        // Telegram has no mime type for a sticker; the format follows from the two flags. Static
        // stickers are WebP, which the shared kind mapping already reads as an image, so someone
        // can ask about one.
        { Sticker: { } sticker } => new TelegramMedia(
            sticker.FileId,
            null,
            sticker switch
            {
                { IsVideo: true } => "video/webm",
                { IsAnimated: true } => "application/x-tgsticker",
                _ => "image/webp"
            },
            sticker.FileSize,
            MediaIntent.Performed),
        { Animation: { } animation } => new TelegramMedia(
            animation.FileId, animation.FileName, animation.MimeType, animation.FileSize,
            MediaIntent.Performed),
        { VideoNote: { } note } => new TelegramMedia(
            note.FileId, null, null, note.FileSize, MediaIntent.Performed),
        { Voice: { } voice } => new TelegramMedia(
            voice.FileId, null, voice.MimeType, voice.FileSize, MediaIntent.Performed),
        { Video: { } video } => new TelegramMedia(
            video.FileId, video.FileName, video.MimeType, video.FileSize, MediaIntent.Attached),
        { Audio: { } audio } => new TelegramMedia(
            audio.FileId, audio.FileName, audio.MimeType, audio.FileSize, MediaIntent.Attached),
        { Document: { } document } => new TelegramMedia(
            document.FileId, document.FileName, document.MimeType, document.FileSize,
            MediaIntent.Attached),
        // A photo arrives as the same picture in several sizes; the largest is the one worth
        // sending. Telegram re-encodes every photo to JPEG and carries neither a mime type nor a
        // filename for one, so the type is a fact about the transport rather than a guess.
        { Photo: { Length: > 0 } sizes } => LargestPhoto(sizes),
        _ => null
    };

    private static TelegramMedia LargestPhoto(IReadOnlyList<PhotoSize> sizes)
    {
        var largest = sizes.MaxBy(size => size.FileSize ?? (long)size.Width * size.Height)!;
        return new TelegramMedia(largest.FileId, null, "image/jpeg", largest.FileSize, MediaIntent.Attached);
    }
}