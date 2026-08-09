using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// DefaultModelAttachmentKinds is what the agent's own model accepts; the per-model values for
// anything a person can switch to ride on PatchableModels. Both are discovered from the provider,
// so the composer can tell before anything is sent whether an attachment is worth the trip.
[PublicAPI]
public record AgentCatalogEntry(
    string Id,
    string Name,
    string? Description,
    string? DefaultModel = null,
    string? DefaultReasoningEffort = null,
    IReadOnlyList<PatchableModel>? PatchableModels = null,
    IReadOnlyList<string>? PatchableReasoningEfforts = null,
    IReadOnlyList<AttachmentKind>? DefaultModelAttachmentKinds = null);