using Domain.DTOs.Channel;

namespace WebChat.Client.Components.Chat;

public enum AttachmentShape
{
    Thumbnail,
    Clip,
    Chip
}

public sealed record ShownAttachment(AttachmentReference Reference, AttachmentShape Shape);

// What a message's attachments look like in the transcript. A clip plays where it sits and its
// stills are left out: a review copy shows what a video model was sent, and WebChat is never handed
// the stills (ADR 0046). Pictures no clip names are photos and show as they always did.
public static class AttachmentDisplay
{
    public static IReadOnlyList<ShownAttachment> For(IReadOnlyList<AttachmentReference>? attachments) =>
        attachments is null
            ? []
            : ClipStills.Without(attachments).Select(a => new ShownAttachment(a, ShapeOf(a.MediaType))).ToList();

    // A thumbnail and a clip need a minted URL to render at all; a chip asks for one when clicked.
    public static IReadOnlyList<string> Previewed(IEnumerable<ShownAttachment> shown) =>
        shown.Where(s => s.Shape != AttachmentShape.Chip).Select(s => s.Reference.Id).ToList();

    private static AttachmentShape ShapeOf(string mediaType) => AttachmentKinds.ForMediaType(mediaType) switch
    {
        AttachmentKind.Image => AttachmentShape.Thumbnail,
        AttachmentKind.Video => AttachmentShape.Clip,
        _ => AttachmentShape.Chip
    };
}