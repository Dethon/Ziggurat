using System.Globalization;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Http;

namespace McpServerSugary.App;

// What a meal's photos must be before anything is kept or sent: the standalone app's rules, with the
// size ceiling set by what the models take. Each reason is shown to her as it is written.
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

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}