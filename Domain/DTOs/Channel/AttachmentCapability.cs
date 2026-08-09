using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// Whether the model a turn would actually run on can read what was attached. The composer asks
// this before the send and the channel server asks it before it emits, from the same catalogue
// and by the same rule, so the two cannot disagree about which model is refusing.
//
// Permissive wherever the catalogue is silent: capability is discovered from the model provider,
// and a lookup that has not answered yet must not remove the feature.
[PublicAPI]
public static class AttachmentCapability
{
    // The effective model is the per-message patch, falling back to the agent's default.
    public static string? EffectiveModel(AgentCatalogEntry? agent, string? patchedModel) =>
        patchedModel ?? agent?.DefaultModel;

    public static IReadOnlyList<AttachmentKind> AcceptedKinds(AgentCatalogEntry? agent, string? patchedModel)
    {
        if (agent is null)
        {
            return AttachmentKinds.All;
        }

        var model = EffectiveModel(agent, patchedModel);
        if (model is null || string.Equals(model, agent.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            return agent.DefaultModelAttachmentKinds ?? AttachmentKinds.All;
        }

        return (agent.PatchableModels ?? [])
                   .FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
                   ?.AcceptedAttachmentKinds
               ?? AttachmentKinds.All;
    }

    // Null when the attachments may go. Otherwise a sentence naming the model and the reason, so
    // the person knows which one to switch to.
    public static string? Refusal(
        AgentCatalogEntry? agent, string? patchedModel, IEnumerable<string> mediaTypes)
    {
        var accepted = AcceptedKinds(agent, patchedModel);
        var refused = mediaTypes
            .Select(AttachmentKinds.ForMediaType)
            .OfType<AttachmentKind>()
            .Distinct()
            .Where(kind => !accepted.Contains(kind))
            .ToList();

        if (refused.Count == 0)
        {
            return null;
        }

        var model = EffectiveModel(agent, patchedModel) ?? "The selected model";
        var kinds = string.Join(" or ", refused.Select(Describe));
        return $"{model} cannot read {kinds}. Pick a different model and your files stay attached.";
    }

    private static string Describe(AttachmentKind kind) =>
        kind == AttachmentKind.Image ? "images" : "documents";
}