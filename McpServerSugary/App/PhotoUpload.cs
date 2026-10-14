using System.Globalization;
using Domain.DTOs.Channel;
using McpServerSugary.Channel;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Http;

namespace McpServerSugary.App;

// What a meal's or a label's photos must be before anything is kept or sent: the standalone app's
// rules, with the size ceiling set by what the models take. Each reason is shown to her as written.
public static class PhotoUpload
{
    private static readonly string[] _mediaTypes = ["image/jpeg", "image/png", "image/webp"];

    public static string? Refusal(IReadOnlyList<IFormFile> photos, UploadSettings settings) => photos switch
    {
        _ when photos.Count > settings.MaxPhotos => $"At most {settings.MaxPhotos} photos per meal.",
        _ when photos.Any(p => !_mediaTypes.Contains(p.ContentType, StringComparer.OrdinalIgnoreCase)) =>
            "Photos must be JPEG, PNG or WebP.",
        _ when photos.Any(p => p.Length == 0) => "A photo arrived empty. Take it again.",
        _ when photos.Any(p => p.Length > settings.MaxPhotoBytes) =>
            $"Each photo must be under {Megabytes(settings.MaxPhotoBytes)}, the most the estimating models take.",
        _ => null
    };

    // The shared rule every channel asks (AttachmentCapability), for the agent the app's turns are
    // routed to, on the model the turn will run on: the agent's own for a meal, the patched one for a
    // label. Its own sentence ends by telling a person to pick another model, which she cannot do
    // from the app, so only its verdict is taken and the reason is said for her.
    public static string? ModelRefusal(IReadOnlyList<IFormFile> photos, AgentCatalogEntry? agent) =>
        BlindModel(photos, agent, patchedModel: null) is { } model
            ? $"{model} cannot read photos, so this meal cannot be estimated from them. " +
              "Describe it in words instead, or count it by hand."
            : null;

    public static string? LabelModelRefusal(IReadOnlyList<IFormFile> photos, AgentCatalogEntry? agent, string labelModel) =>
        BlindModel(photos, agent, labelModel) is { } model
            ? $"{model} cannot read photos, so this label cannot be read. Add the food by hand."
            : null;

    private static string? BlindModel(IReadOnlyList<IFormFile> photos, AgentCatalogEntry? agent, string? patchedModel) =>
        AttachmentCapability.Refusal(agent, patchedModel, photos.Select(p => p.ContentType)) is null
            ? null
            : AttachmentCapability.EffectiveModel(agent, patchedModel) ?? "The estimating model";

    // One at a time and in the order she took them, so the references keep the order of the photos.
    public static async Task<IReadOnlyList<AttachmentReference>> StoreAsync(
        string conversationId, IReadOnlyList<IFormFile> photos, UploadStore uploads, CancellationToken ct)
    {
        var stored = new List<AttachmentReference>(photos.Count);
        foreach (var photo in photos)
        {
            using var buffer = new MemoryStream();
            await photo.CopyToAsync(buffer, ct);
            stored.Add(await uploads.SaveAsync(conversationId, photo.FileName, photo.ContentType, buffer.ToArray(), ct));
        }

        return stored;
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}