using Domain.DTOs.Channel;
using WebChat.Client.State.AgentSettings;

namespace WebChat.Client.State.Composer;

// What the composer holds, answered for the two callers that ask: the send, which needs the files
// that will travel, and the input, which needs to know whether it may go at all. The capability
// rule itself lives in AttachmentCapability, which the channel server asks too, so the refusal a
// person sees and the refusal the server would give are about the same model.
public static class ComposerSelectors
{
    // Everything that has been accepted, whether or not it has finished uploading: a file still
    // going up counts against the per-message maximum, and one already refused does not.
    public static IEnumerable<ComposerAttachment> Sendable(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Where(a => a.Status != AttachmentStatus.Failed);

    // Only the files that finished. One still uploading has no reference to send yet, and a
    // refused one never will; both stay in the composer rather than silently going along.
    public static IReadOnlyList<ComposerAttachment> Ready(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Where(a => a is { Status: AttachmentStatus.Ready, Reference: not null }).ToList();

    public static IReadOnlyList<AttachmentReference>? References(
        IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Count == 0 ? null : attachments.Select(a => a.Reference!).ToList();

    // Null when the send may go ahead. Otherwise the sentence a person reads: which model is
    // refusing and why. The files stay attached either way, so the fix is switching model.
    public static string? CapabilityRefusal(
        AgentSettingsState settings,
        IReadOnlyList<AgentCatalogEntry> agents,
        string agentId,
        IReadOnlyList<ComposerAttachment> attachments)
    {
        // Only what the send would actually carry. A file the composer already refused is going
        // nowhere, so letting it block the send would leave a person unable to send until they
        // worked out that a failed chip was the reason.
        var going = Sendable(attachments).ToList();
        if (going.Count == 0)
        {
            return null;
        }

        var refusal = AttachmentCapability.Refusal(
            agents.FirstOrDefault(a => a.Id == agentId),
            settings.ByAgent.GetValueOrDefault(agentId)?.Model,
            going.Select(a => a.MediaType));

        // The composer keeps the files, unlike the server's own guard, so it is the composer that
        // gets to promise it.
        return refusal is null ? null : $"{refusal} Your files stay attached.";
    }

    public static bool HasUploadInFlight(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Any(a => a.Status == AttachmentStatus.Uploading);

    // One control on the right, always the one the person is about to use. Cancel while the reply
    // runs, as it always has been; the microphone only where Send would have nothing to send, so
    // no composer width is lost to a third button and the control under the thumb is never dead.
    // Cancel keeps the precedence it always had: while the reply runs, Send could only ever be
    // dead, and that is true whatever the microphone is doing. Below it, an open microphone holds
    // the spot even against text — the strip is what is on screen, and the control must not change
    // under the finger holding it.
    public static SendControl SendControl(
        bool isStreaming, string? text, int readyAttachments, DictationStatus dictation) => true switch
        {
            _ when isStreaming => Composer.SendControl.Cancel,
            _ when dictation is DictationStatus.Recording or DictationStatus.Latched =>
                Composer.SendControl.Microphone,
            _ when !string.IsNullOrWhiteSpace(text) || readyAttachments > 0 => Composer.SendControl.Send,
            _ => Composer.SendControl.Microphone
        };

    // Words are added to what was typed rather than replacing it: dictating must never destroy
    // half a sentence somebody had already thumbed in.
    public static string Append(string? existing, string transcript) =>
        string.IsNullOrWhiteSpace(existing) ? transcript : $"{existing.TrimEnd()} {transcript}";
}