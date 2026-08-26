using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Infrastructure.Clients.Browser;

// The C# half of the fetch script's answer. The script serializes one JSON object -- media type,
// label, base64 bytes -- because a label is somebody else's text and a hand-delimited payload
// cannot survive it: an alt reading "Photo | Getty Images" once shifted its own tail into the
// base64 field, and the picture came back as the site refusing.
//
// Never throws: every string the script did not write is a refusal, not an exception out of the
// fetch loop.
internal static class ImageFetchPayload
{
    public static FetchedImage Parse(string imageRef, string? payload)
    {
        if (payload is null)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.NotAnImageRef);
        }

        if (payload == "never-loaded")
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.NeverLoaded);
        }

        try
        {
            var node = JsonNode.Parse(payload)?.AsObject();
            var mediaType = (string?)node?["mediaType"];
            var data = (string?)node?["data"];
            if (string.IsNullOrEmpty(mediaType) || data is null)
            {
                return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
            }

            var label = (string?)node?["label"];
            return new FetchedImage(
                imageRef,
                mediaType,
                Convert.FromBase64String(data),
                ImageFetchStatus.Success,
                Label: string.IsNullOrEmpty(label) ? null : label);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // The "error" sentinel lands here too: it is not JSON, and it means the same thing.
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
        }
    }
}