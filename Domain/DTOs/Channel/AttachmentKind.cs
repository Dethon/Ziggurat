using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// What a model has to accept for an attachment of that kind to be worth sending. Two kinds, not a
// media type list: capability belongs to the model, and providers describe it at this width.
[PublicAPI]
public enum AttachmentKind
{
    Image,
    Document
}

[PublicAPI]
public static class AttachmentKinds
{
    public static IReadOnlyList<AttachmentKind> All { get; } =
        [AttachmentKind.Image, AttachmentKind.Document];

    public static bool IsImage(string? mediaType) => ForMediaType(mediaType) == AttachmentKind.Image;

    public static AttachmentKind? ForMediaType(string? mediaType) => mediaType switch
    {
        null => null,
        "application/pdf" => AttachmentKind.Document,
        _ when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => AttachmentKind.Image,
        _ => null
    };
}