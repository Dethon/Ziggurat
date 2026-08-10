using Domain.DTOs.Channel;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// What one Telegram message — or one album — becomes on the way in: attachment references the
// agent can fetch back.
//
// It holds no clock, so every rule here is a pure function of the update. The album buffer beside
// it owns the only thing on this channel that needs time.
internal static class AttachmentIntake
{
    public static IReadOnlyList<AttachmentReference> Read(string agentId, IReadOnlyList<Message> messages) =>
        [.. messages.Select(message => Read(agentId, message)).OfType<AttachmentReference>()];

    private static AttachmentReference? Read(string agentId, Message message)
    {
        if (TelegramMedia.Read(message) is not { } media)
        {
            return null;
        }

        var mediaType = MediaTypes.Resolve(media);
        if (AttachmentKinds.ForMediaType(mediaType) is null)
        {
            return null;
        }

        return new AttachmentReference
        {
            // A Telegram file handle is per-bot, so the reference names the agent whose bot can
            // fetch it. That also scopes it for free: another agent's bot cannot use this handle.
            Id = $"{agentId}/{media.FileId}",
            FileName = MediaTypes.NameFor(media, message.Id, mediaType),
            MediaType = mediaType!,
            SizeBytes = media.SizeBytes ?? 0
        };
    }
}