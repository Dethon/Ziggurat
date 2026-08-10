using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

// What each model accepts, discovered from the provider rather than configured. Permissive when
// nothing is known: a transient problem at the provider must not remove a feature from everyone,
// and an attachment the model turns out not to accept fails later as a refusal.
[PublicAPI]
public interface IModelCapabilityCatalog
{
    IReadOnlyList<AttachmentKind> GetAcceptedAttachmentKinds(string modelId);
}