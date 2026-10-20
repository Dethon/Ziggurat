using Domain.DTOs.Channel;
using Domain.Exceptions;

namespace Domain.Agents;

// Which of a message's attachments a model is sent when the message carries a clip. The clip's
// reference names its stills, pictures of the same message, and the model gets one or the other,
// never both: the clip if it watches video, the stills and every other picture if it only sees
// pictures. A model that would see nothing of the clip has the turn refused rather than answering
// as if no clip had been sent.
//
// Past the hydration distance only placeholders go, and one for the clip says what eight for its
// frames would, so there the clip stands for its stills whatever the model.
internal static class ClipSelection
{
    public static IReadOnlyList<AttachmentReference> For(
        IReadOnlyList<AttachmentReference> attachments, ModelInputs inputs, bool withinDepth)
    {
        var clips = attachments.Where(a => AttachmentKinds.IsVideo(a.MediaType)).ToList();
        if (clips.Count == 0)
        {
            return attachments;
        }

        if (inputs.Video || !withinDepth)
        {
            return ClipStills.Without(attachments);
        }

        if (!inputs.Images)
        {
            throw new AttachmentRefusedException(
                $"{inputs.Model} takes neither video nor images, so it cannot see {Names(clips)}. " +
                "Pick a different model and send it again.");
        }

        var present = attachments.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var unseen = clips.Where(c => !(c.Stills ?? []).Any(present.Contains)).ToList();
        if (unseen.Count > 0)
        {
            throw new AttachmentRefusedException(
                $"{inputs.Model} does not take video, and {Names(unseen)} came with no stills to show it " +
                "instead. Pick a model that takes video and send it again.");
        }

        return attachments.Where(a => !AttachmentKinds.IsVideo(a.MediaType)).ToList();
    }

    private static string Names(IEnumerable<AttachmentReference> clips) =>
        string.Join(", ", clips.Select(c => c.FileName));
}