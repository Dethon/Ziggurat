using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Infrastructure.HtmlProcessing;

namespace Infrastructure.Clients.Browser;

// The C# half of the fetch script's answers. The script serializes JSON objects -- because the
// strings it carries are somebody else's text, and a hand-delimited payload cannot survive them:
// an alt reading "Photo | Getty Images" once shifted its own tail into the base64 field, and the
// picture came back as the site refusing.
//
// Never throws: every string the script did not write is a refusal, not an exception out of the
// fetch loop.
internal static class ImageFetchPayload
{
    // The locate script's answer: the strings the page offers about the picture, verbatim, for
    // the one ladder to name. Garbage in any field reads as that field unspoken.
    public static ImageLabelFacts Facts(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            return new ImageLabelFacts(
                Alt: (string?)node?["alt"],
                Caption: (string?)node?["caption"],
                Title: (string?)node?["title"],
                LinkText: (string?)node?["linkText"],
                Src: (string?)node?["src"],
                Width: (int?)node?["w"] ?? 0,
                Height: (int?)node?["h"] ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new ImageLabelFacts(null, null, null, null, null, 0, 0);
        }
    }

    public static FetchedImage Parse(string imageRef, string? payload, string? label)
    {
        if (payload is null)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.NotAnImageRef);
        }

        if (payload == "never-loaded")
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.NeverLoaded);
        }

        if (Tainted(payload, out _))
        {
            // The fetch loop is meant to catch this before parsing and read the pixels off the
            // screen; a tainted payload reaching here uncaught is still a refusal, not a throw.
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
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

            return new FetchedImage(
                imageRef,
                mediaType,
                Convert.FromBase64String(data),
                ImageFetchStatus.Success,
                Label: label);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // The "error" sentinel lands here too: it is not JSON, and it means the same thing.
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
        }
    }

    // A canvas the browser shows but will not let script read: the script answers tainted with
    // the address it resolved, and the C# side re-requests that address from outside the page —
    // where CORS has no say — falling back to an element screenshot of the pixels already on
    // screen.
    public static bool Tainted(string? payload, out string? url)
    {
        url = null;
        if (payload is null || !payload.StartsWith('{'))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(payload)?.AsObject();
            if ((bool?)node?["tainted"] is not true)
            {
                return false;
            }

            url = NonEmpty((string?)node?["url"]);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}