using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// One chat model the Lemonade chat host says it has, as the host describes it: the id it answers
// to (bare, not yet namespaced), what it accepts, and the context window it was loaded with —
// null when the host did not say.
[PublicAPI]
public record LemonadeModel(
    string Id,
    IReadOnlyList<AttachmentKind> AcceptedAttachmentKinds,
    int? ContextWindow);