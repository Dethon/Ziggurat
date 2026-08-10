using Domain.DTOs.Channel;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// A file that could not be taken, and the message it arrived in, so the reply can quote what
// failed rather than leaving someone to guess which of five photos it was about.
internal sealed record IntakeRefusal(int MessageId, string Reason);

internal sealed record Intake(
    IReadOnlyList<AttachmentReference> Attachments,
    IReadOnlyList<IntakeRefusal> Refusals);

// What one Telegram message — or one album — becomes on the way in: attachment references the
// agent can fetch back, and the files that could not be taken.
//
// It holds no clock, so every rule here is a pure function of the update. The album buffer beside
// it owns the only thing on this channel that needs time.
internal static class AttachmentIntake
{
    // Telegram's own ceiling, quoted from its bot FAQ: getFile "will only work with files of up to
    // 20 MB in size". Not a setting — it is a fact about the transport, and lifting it means
    // running a local bot API server.
    public const long MaxDownloadBytes = 20L * 1024 * 1024;

    public static Intake Read(string agentId, IReadOnlyList<Message> messages)
    {
        var results = messages.Select(message => Read(agentId, message)).ToList();
        return new Intake(
            [.. results.Select(result => result.Attachment).OfType<AttachmentReference>()],
            [.. results.Select(result => result.Refusal).OfType<IntakeRefusal>()]);
    }

    private static (AttachmentReference? Attachment, IntakeRefusal? Refusal) Read(string agentId, Message message)
    {
        if (TelegramMedia.Read(message) is not { } media)
        {
            return (null, null);
        }

        var mediaType = MediaTypes.Resolve(media);
        var name = MediaTypes.NameFor(media, message.Id, mediaType);

        if (AttachmentKinds.ForMediaType(mediaType) is null)
        {
            return (null, new IntakeRefusal(
                message.Id, $"I could not take \"{name}\": I can only read images and PDFs."));
        }

        // Read off the update, so a file too large to fetch is refused while the person is still
        // standing there rather than discovered by a download that was never going to work.
        if (media.SizeBytes > MaxDownloadBytes)
        {
            return (null, new IntakeRefusal(
                message.Id,
                $"I could not take \"{name}\": it is {Megabytes(media.SizeBytes.Value)} and Telegram only " +
                "lets me download files up to 20 MB."));
        }

        return (new AttachmentReference
        {
            // A Telegram file handle is per-bot, so the reference names the agent whose bot can
            // fetch it. That also scopes it for free: another agent's bot cannot use this handle.
            Id = $"{agentId}/{media.FileId}",
            FileName = name,
            MediaType = mediaType!,
            SizeBytes = media.SizeBytes ?? 0
        }, null);
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";
}