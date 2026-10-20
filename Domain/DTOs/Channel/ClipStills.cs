using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// A clip's stills are pictures of the same message that stand in for the clip where it cannot be
// watched (ADR 0045). Wherever the clip itself goes — to a model that takes video, into a review
// copy, onto the transcript — they are left behind, so this is the one reading of which they are.
[PublicAPI]
public static class ClipStills
{
    public static IReadOnlyList<AttachmentReference> Without(IReadOnlyList<AttachmentReference> attachments)
    {
        var stills = attachments
            .Where(a => AttachmentKinds.IsVideo(a.MediaType))
            .SelectMany(clip => clip.Stills ?? [])
            .ToHashSet(StringComparer.Ordinal);
        return stills.Count == 0 ? attachments : attachments.Where(a => !stills.Contains(a.Id)).ToList();
    }
}