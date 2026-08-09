using Domain.DTOs.Channel;
using WebChat.Client.State.AgentSettings;

namespace WebChat.Client.State.Composer;

// The effective model is the per-message choice, falling back to the agent's default. The rule
// itself lives in AttachmentCapability, which the channel server asks too, so the refusal a
// person sees and the refusal the server would give are about the same model.
public static class ComposerSelectors
{
    public static string? EffectiveModel(
        AgentSettingsState settings, IReadOnlyList<AgentCatalogEntry> agents, string agentId) =>
        AttachmentCapability.EffectiveModel(Agent(agents, agentId), Patched(settings, agentId));

    public static IReadOnlyList<AttachmentKind> AcceptedKinds(
        AgentSettingsState settings, IReadOnlyList<AgentCatalogEntry> agents, string agentId) =>
        AttachmentCapability.AcceptedKinds(Agent(agents, agentId), Patched(settings, agentId));

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
            Agent(agents, agentId), Patched(settings, agentId), going.Select(a => a.MediaType));

        // The composer keeps the files, unlike the server's own guard, so it is the composer that
        // gets to promise it.
        return refusal is null ? null : $"{refusal} Your files stay attached.";
    }

    // Everything that has been accepted, whether or not it has finished uploading: a file still
    // going up counts against the per-message maximum, and one already refused does not.
    public static IEnumerable<ComposerAttachment> Sendable(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Where(a => a.Status != AttachmentStatus.Failed);

    public static bool HasUploadInFlight(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Any(a => a.Status == AttachmentStatus.Uploading);

    private static AgentCatalogEntry? Agent(IReadOnlyList<AgentCatalogEntry> agents, string agentId) =>
        agents.FirstOrDefault(a => a.Id == agentId);

    private static string? Patched(AgentSettingsState settings, string agentId) =>
        settings.ByAgent.GetValueOrDefault(agentId)?.Model;
}