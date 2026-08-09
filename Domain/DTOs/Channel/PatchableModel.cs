using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// AcceptedAttachmentKinds is discovered from the model provider rather than configured, so it is
// stamped on the way into the catalogue and is null in configuration.
[PublicAPI]
public record PatchableModel(
    string Id,
    string Name,
    IReadOnlyList<AttachmentKind>? AcceptedAttachmentKinds = null);