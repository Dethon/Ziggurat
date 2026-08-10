using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// What a person is told when a file cannot be attached. The composer refuses at pick time and the
// endpoint refuses on arrival, and the two must not disagree about the reason: a person who reads
// one wording in the composer and a different one from the server learns that the two checks are
// different rules, which they are not.
[PublicAPI]
public static class AttachmentRefusals
{
    public static string TooLarge(string fileName, long maxBytes) =>
        $"{fileName} is larger than the {maxBytes / (1024 * 1024)} MB limit.";

    public static string UnsupportedKind(string what) =>
        $"{what} is not a kind this chat accepts; attach an image or a PDF.";

    public static string TooManyFiles(int maximum) =>
        $"A message takes at most {maximum} files.";

    // The composer applies the same rules before a byte moves; the endpoint applies them again on
    // arrival, because a ticket is all that stands between a public hostname and public disk.
    public static string? For(string fileName, string mediaType, long sizeBytes, AttachmentLimits limits) =>
        Refusal(fileName, mediaType, sizeBytes, limits)?.Message;

    // The kind is for the endpoint, which answers each refusal with its own status code; the
    // composer only ever shows the message.
    public static AttachmentRefusal? Refusal(
        string fileName, string mediaType, long sizeBytes, AttachmentLimits limits)
    {
        if (sizeBytes > limits.MaxBytesPerFile)
        {
            return new AttachmentRefusal(
                AttachmentRefusalKind.TooLarge, TooLarge(fileName, limits.MaxBytesPerFile));
        }

        return limits.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
            ? null
            : new AttachmentRefusal(AttachmentRefusalKind.UnsupportedKind, UnsupportedKind(fileName));
    }

    public static bool IsImage(string? mediaType) =>
        AttachmentKinds.ForMediaType(mediaType) == AttachmentKind.Image;
}

[PublicAPI]
public enum AttachmentRefusalKind
{
    TooLarge,
    UnsupportedKind
}

[PublicAPI]
public sealed record AttachmentRefusal(AttachmentRefusalKind Kind, string Message);