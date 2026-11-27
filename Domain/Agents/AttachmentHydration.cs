using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// Putting an attachment's bytes back where its reference sits, on the way to the model and never
// on the way in. It sits beside TurnDecoration for the same reason: what the model reads that the
// history does not hold belongs in one place, and the copy it produces is thrown away with the
// request (ADR 0020).
//
// Every attachment is sent for the whole conversation (user, 2026-11-27): swapping one for a
// placeholder rewrites the prompt where it sits and bills everything after it again uncached, while
// a picture left in the cached prefix costs a tenth of its tokens. The one bound is the providers'
// own caps on a request: past the newest maxPictures, or past maxPictureBytes of them (a request is
// uploaded whole on every tool round, and Anthropic refuses one over 32 MB), counted from the end,
// a message's pictures become placeholders naming each file — as does any reference whose bytes are gone — so
// the model says the file is gone rather than inventing what it contained. Only newer pictures are
// ever added, so a message past the cap stays past it and costs one cache miss, once. A document is
// no picture and is always sent.
//
// It widened rather than splitting when the model learned to read images: an image the model asked
// for is a reference whose bytes have to be put back too, so it counts toward the same cap and
// takes the same shape of placeholder. Where the bytes go differs, and that is ReadImageHydration's half.
public static class AttachmentHydration
{
    public const int DefaultMaxPictures = 100;
    public const long DefaultMaxPictureBytes = 20L * 1024 * 1024;

    // turn: the turn this send is for, whose identity every fetch is signed as, the earlier messages'
    // files included: a follow-up's photos are fetched for the person asking now, never as whoever
    // sent them, and a channel keeping a household's files hands them to that household's turns alone.
    public static async Task<IReadOnlyList<ChatMessage>> ApplyAsync(
        IReadOnlyList<ChatMessage> messages,
        IAttachmentSource? source,
        ConversationContext? turn,
        ReadImageContext readImages,
        int maxPictures,
        long maxPictureBytes,
        CancellationToken ct,
        ModelInputs? inputs = null)
    {
        // Parsed once for the whole send. Recognising an image read means serializing every tool
        // result in a message, so asking twice would make every send pay it again for every tool
        // call in the history — reads, searches and everything else included.
        var imageReads = messages.Select(ReadImageHydration.Reads).ToList();

        var hydratesAttachments = source is not null && messages.Any(NeedsHydrating);
        var expandsImages = imageReads.Any(reads => reads.Count > 0);

        if (!hydratesAttachments && !expandsImages)
        {
            return messages;
        }

        var takes = inputs ?? ModelInputs.Everything("The model");
        var withinCap = WithinCap(messages, imageReads, takes, maxPictures, maxPictureBytes);

        // The turn's own message is the one a model that cannot see its files is refused over; an
        // earlier one, kept in every request for good, only names what this model is not shown.
        var own = messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);

        var hydrated = new List<ChatMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];

            // The two rewrites touch different kinds of message — attachments sit on a person's
            // turn, read images on a tool message — so each message takes at most one of them.
            hydrated.Add(message switch
            {
                _ when source is not null && NeedsHydrating(message) =>
                    await HydrateAsync(message, source, turn, takes, withinCap[index], index == own, ct),
                _ when imageReads[index].Count > 0 =>
                    await ReadImageHydration.ExpandAsync(
                        message, imageReads[index], readImages, withinCap[index], ct),
                _ => message
            });
        }

        return hydrated;
    }

    // Whether each message's pictures fit under both caps, counting from the newest message back. A
    // message goes whole, never split, so a turn's photos are shown or named together; one with no
    // pictures always fits.
    private static bool[] WithinCap(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<IReadOnlyList<ReadImageReference>> imageReads,
        ModelInputs inputs,
        int maxPictures,
        long maxPictureBytes)
    {
        var weights = messages
            .Select((message, index) =>
            {
                var attached = ClipSelection.PicturesOf(message.GetAttachments() ?? [], inputs);
                var read = inputs.Images ? imageReads[index] : [];
                return (Count: attached.Count + read.Count,
                    Bytes: attached.Sum(a => a.SizeBytes) + read.Sum(r => r.SizeBytes));
            })
            .ToList();

        // A running total from the end, in one pass: every send runs this over the whole history.
        var withinCap = new bool[weights.Count];
        var (count, bytes) = (0, 0L);
        for (var index = weights.Count - 1; index >= 0; index--)
        {
            count += weights[index].Count;
            bytes += weights[index].Bytes;
            withinCap[index] = weights[index].Count == 0 || (count <= maxPictures && bytes <= maxPictureBytes);
        }

        return withinCap;
    }

    private static bool NeedsHydrating(ChatMessage message) =>
        message.GetAttachments() is { Count: > 0 }
        || message.GetSandboxPaths() is { Count: > 0 }
        || message.GetLandingFailures() is { Count: > 0 };

    private static async Task<ChatMessage> HydrateAsync(
        ChatMessage message, IAttachmentSource source, ConversationContext? turn, ModelInputs inputs, bool withinCap,
        bool own, CancellationToken ct)
    {
        // Decided before any byte is fetched, so a still the model will not be sent is never asked
        // for and a refused turn costs the channel nothing.
        var attachments = ClipSelection.For(
            message.GetAttachments() ?? [], inputs, withinCap, refuses: own);
        var channelId = message.GetAttachmentChannelId();
        var shown = attachments.Count > 0 && !string.IsNullOrWhiteSpace(channelId)
            ? await ShowAsync(
                attachments, source, channelId!, message.GetAttachmentHouseholdId(), turn, withinCap,
                own ? null : inputs, ct)
            : [];

        // The files first, then where they landed: a path named ahead of a picture the model could
        // already see read as an errand to fetch it, and jonas file_read a photo it had just
        // described (2026-10-18). Named as shown only when every file went, because the sandbox
        // copy is the only one the model has of a file that did not.
        var contents = new List<AIContent>(shown);

        // Where the files landed in the sandbox, named so the model can act on them without
        // being told a tool exists. It outlives a picture past the cap, because the file stays in
        // the sandbox after the bytes stop being sent.
        if (message.GetSandboxPaths() is { Count: > 0 } paths)
        {
            contents.Add(new TextContent(AttachmentLanding.Describe(
                paths, shown: shown.Count > 0 && shown.All(c => c is DataContent))));
        }

        // Which files never got there, so the model's answers account for them rather than planning
        // commands against paths that do not exist.
        if (message.GetLandingFailures() is { Count: > 0 } failed)
        {
            contents.Add(new TextContent(AttachmentLanding.DescribeFailures(failed)));
        }

        return Append(message, contents);
    }

    private static async Task<IReadOnlyList<AIContent>> ShowAsync(
        IReadOnlyList<AttachmentReference> attachments,
        IAttachmentSource source,
        string channelId,
        string? householdId,
        ConversationContext? turn,
        bool withinCap,
        ModelInputs? shownOnly,
        CancellationToken ct)
    {
        var contents = new List<AIContent>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (shownOnly is not null && !Takes(shownOnly, attachment))
            {
                contents.Add(new TextContent(NotTakenPlaceholder(attachment)));
                continue;
            }

            var bytes = withinCap
                ? await source.FetchAsync(channelId, householdId, attachment.Id, turn, ct)
                : null;
            contents.Add(bytes is not null
                ? new DataContent(bytes, attachment.MediaType)
                : new TextContent(Placeholder(attachment)));
        }

        return contents;
    }

    private static bool Takes(ModelInputs inputs, AttachmentReference attachment) =>
        AttachmentKinds.ForMediaType(attachment.MediaType) switch
        {
            AttachmentKind.Image => inputs.Images,
            AttachmentKind.Video => inputs.Video,
            _ => true
        };

    private static ChatMessage Append(ChatMessage message, IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return message;
        }

        var copy = message.Clone();
        copy.Contents = [.. copy.Contents, .. contents];
        return copy;
    }

    private static string NotTakenPlaceholder(AttachmentReference attachment) =>
        $"[The attachment {attachment.FileName} ({attachment.MediaType}) was not shown: the model running " +
        "this turn does not accept images or video. Don't describe what it contained from memory.]";

    private static string Placeholder(AttachmentReference attachment) =>
        $"[The attachment {attachment.FileName} ({attachment.MediaType}) is no longer available to you. " +
        "Say so if asked about it rather than describing what it contained.]";
}