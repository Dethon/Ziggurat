using Domain.DTOs.Channel;
using Domain.Exceptions;

namespace Domain.Agents;

// Which of a message's attachments a model is sent when the message carries a clip. The clip's
// reference names its stills, pictures of the same message, and the model gets one or the other,
// never both: the clip if it watches video, the stills and every other picture if it only sees
// pictures. A model that would see nothing of the clip has the turn refused rather than answering
// as if no clip had been sent.
//
// Past the picture cap only placeholders go, and one for the clip says what eight for its frames
// would, so there the clip stands for its stills whatever the model.
internal static class ClipSelection
{
    // refuses: false for an earlier message than the turn's own — a clip sent before a turn moved to a
    // model that cannot see it is named to that turn, never a reason to refuse it.
    public static IReadOnlyList<AttachmentReference> For(
        IReadOnlyList<AttachmentReference> attachments, ModelInputs inputs, bool withinCap, bool refuses = true)
    {
        var clips = attachments.Where(a => AttachmentKinds.IsVideo(a.MediaType)).ToList();
        if (clips.Count == 0)
        {
            return attachments;
        }

        if (inputs.Video || !withinCap)
        {
            return ClipStills.Without(attachments);
        }

        if (!refuses && (!inputs.Images || clips.Any(c => c.Stills is not { Count: > 0 })))
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

    // How many pictures the message puts in a request: its images, a clip's stills among them unless
    // the model watches the clip instead. Never refuses: the cap is counted before a message is known
    // to be shown, and one past it is never refused over a clip it will not be sent.
    public static int PicturesIn(IReadOnlyList<AttachmentReference> attachments, ModelInputs inputs) =>
        !inputs.Images
            ? 0
            : (inputs.Video ? ClipStills.Without(attachments) : attachments)
            .Count(a => AttachmentKinds.IsImage(a.MediaType));

    private static string Names(IEnumerable<AttachmentReference> clips) =>
        string.Join(", ", clips.Select(c => c.FileName));
}