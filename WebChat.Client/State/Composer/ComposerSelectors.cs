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
        IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Count == 0
            ? null
            : AttachmentCapability.Refusal(
                Agent(agents, agentId),
                Patched(settings, agentId),
                attachments.Select(a => a.MediaType));

    public static bool HasUploadInFlight(IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Any(a => a.Status == AttachmentStatus.Uploading);

    private static AgentCatalogEntry? Agent(IReadOnlyList<AgentCatalogEntry> agents, string agentId) =>
        agents.FirstOrDefault(a => a.Id == agentId);

    private static string? Patched(AgentSettingsState settings, string agentId) =>
        settings.ByAgent.GetValueOrDefault(agentId)?.Model;
}