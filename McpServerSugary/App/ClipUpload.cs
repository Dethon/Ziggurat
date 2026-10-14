using System.Globalization;
using Domain.DTOs.Channel;
using McpServerSugary.Channel;
using McpServerSugary.Clips;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Http;

namespace McpServerSugary.App;

// What a meal's clip must be before it is read: one, not empty, and no heavier than a phone's
// twenty seconds. Whether it is a clip at all, and how long, is the reading's to say (ClipProcessor),
// since a phone's own label for the file is no guide: an iPhone's is video/quicktime, some browsers
// send none. Each reason is shown to her as written.
public static class ClipUpload
{
    // What every clip is once made small, whatever the phone recorded.
    public const string MediaType = "video/mp4";

    public static string? Refusal(IReadOnlyList<IFormFile> clips, UploadSettings settings) => clips switch
    {
        { Count: > 1 } => "One clip per meal.",
        _ when clips.Any(c => c.Length == 0) => "The clip arrived empty. Film it again.",
        _ when clips.Any(c => c.Length > settings.MaxClipBytes) =>
            $"A clip must be under {Megabytes(settings.MaxClipBytes)}. Film a shorter one.",
        _ => null
    };

    // The clip and its stills, kept beside the meal's photos under the same conversation, each an
    // attachment of its own that the agent fetches like any other. The clip names its stills, so the
    // send can give a model the one or the other (ADR-0045); each still's name says the moment of the
    // clip it was taken at.
    public static async Task<IReadOnlyList<AttachmentReference>> StoreAsync(
        string conversationId, PreparedClip.Ready clip, UploadStore uploads, CancellationToken ct)
    {
        var stills = new List<AttachmentReference>(clip.Stills.Count);
        foreach (var (still, index) in clip.Stills.Select((s, i) => (s, i)))
        {
            var name = $"still-{index + 1}-{still.At.ToString("0.00", CultureInfo.InvariantCulture)}s.jpg";
            stills.Add(await uploads.SaveAsync(conversationId, name, "image/jpeg", still.Jpeg, ct));
        }

        var stored = await uploads.SaveAsync(conversationId, "meal.mp4", MediaType, clip.Clip, ct);
        return [stored with { Stills = [.. stills.Select(s => s.Id)] }, .. stills];
    }

    // The request may carry a phone's clip beside its photos: more than a web server takes in one
    // request unless told, and no more than these two ceilings and room for the words.
    public static long MaxRequestBytes(UploadSettings settings) =>
        settings.MaxClipBytes + settings.MaxPhotos * settings.MaxPhotoBytes + 1024 * 1024;

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}