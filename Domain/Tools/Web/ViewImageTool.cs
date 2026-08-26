using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.Web;

// One picture that arrived, or the wall it hit. The envelope precedes its own bytes, so the model
// reads which image it is about before it looks at it.
public record ViewImageToolResult(
    JsonNode Envelope,
    IReadOnlyList<ViewedImage> Images,
    IReadOnlyList<string> Notes);

public record ViewedImage(string Ref, string MediaType, byte[] Bytes, JsonNode Envelope);

public class ViewImageTool(IWebBrowser browser)
{
    public const string Name = "view_image";

    // Comparing pictures is the ordinary case and should not cost one round trip each. The cap
    // counts images rather than bytes: bytes are the truer bound, and a count is the one the model
    // can reason about before it calls (docs/adr/0033).
    public const int MaxPerCall = 8;

    protected const string Description =
        """
        Looks at images on the page you last browsed, by the refs web_browse listed.
        Pass the refs from the image entries in the page text — they look like i-1, i-2.
        These are not the e-1 style refs web_action uses; the two are separate namespaces.

        Up to 8 images per call. Ask for more and the first 8 come back with the rest named,
        so nothing is lost — call again for those.

        The refs live in the browser session that listed them and expire with it. If a ref no
        longer resolves, browse the page again to get fresh ones.
        """;

    protected async Task<ViewImageToolResult> RunAsync(
        string sessionId,
        IReadOnlyList<string> refs,
        bool modelAcceptsImages,
        CancellationToken ct)
    {
        if (refs.Count == 0)
        {
            return Refusal(
                sessionId,
                ToolError.Codes.InvalidArgument,
                "No image refs were given. Pass the refs from the image entries in the page text.");
        }

        // Asked before anything is fetched: bytes the model cannot be shown are bytes nobody
        // should pay to transfer or store.
        if (!modelAcceptsImages)
        {
            return Refusal(
                sessionId,
                ToolError.Codes.UnsupportedOperation,
                "The model running this turn does not accept images, so no picture can be shown. "
                + "Ask again on a model that does rather than retrying on this one.");
        }

        // The cap cuts before shape is examined: what it defers is answered when it is actually
        // asked for, so one stray ref in the tail cannot refuse the eight ahead of it.
        var asked = refs.Take(MaxPerCall).ToList();
        var deferred = refs.Skip(MaxPerCall).ToList();

        // A ref's shape is what says which tool it was meant for, so the other kind is turned away
        // by name here rather than failing to be found on the page.
        if (asked.Where(r => !ImageRef.IsImageRef(r)).ToList() is { Count: > 0 } foreign)
        {
            return Refusal(
                sessionId,
                ToolError.Codes.InvalidArgument,
                $"{string.Join(", ", foreign)} {(foreign.Count == 1 ? "is not an image ref" : "are not image refs")}. "
                + "Image refs look like i-1 and come from the image entries in the page text; "
                + "e-style refs belong to web_action.");
        }

        var fetched = await browser.FetchImagesAsync(new ImageFetchRequest(sessionId, asked), ct);

        if (fetched.SessionMissing)
        {
            return Refusal(
                sessionId,
                ToolError.Codes.SessionNotFound,
                "That browser session has expired, so its image refs no longer mean anything. "
                + "Browse the page again and use the refs it lists.");
        }

        var images = fetched.Images
            .Where(i => i is { Status: ImageFetchStatus.Success, Bytes: not null, MediaType: not null })
            .Select(ToViewed)
            .ToList();

        var notes = fetched.Images
            .Where(i => i.Status != ImageFetchStatus.Success)
            .Select(NoteFor)
            .ToList();

        var envelope = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = sessionId,
            ["imageCount"] = images.Count
        };

        if (deferred.Count > 0)
        {
            // Partial success is success: the call progresses and learns the rule instead of
            // bouncing off it.
            envelope["deferredRefs"] = new JsonArray(deferred.Select(r => (JsonNode)r!).ToArray());
            envelope["message"] =
                $"Only {MaxPerCall} images can be fetched per call. Call view_image again for the rest.";
        }

        if (notes.Count > 0)
        {
            envelope["notes"] = new JsonArray(notes.Select(n => (JsonNode)n!).ToArray());
        }

        return new ViewImageToolResult(envelope, images, notes);
    }

    private static ViewedImage ToViewed(FetchedImage image) =>
        new(image.Ref,
            image.MediaType!,
            image.Bytes!,
            FsResultContract.ToNode(new PageImageResult
            {
                ImageRef = image.Ref,
                // The name the page gave it, so a note left in its place afterwards names what the
                // model actually asked to see rather than a handle that is gone.
                Label = image.Label ?? image.Ref,
                MediaType = image.MediaType!,
                SizeBytes = image.Bytes!.Length,
                Shown = true
            }));

    private static string NoteFor(FetchedImage image) => image.Status switch
    {
        ImageFetchStatus.NotAnImageRef =>
            $"{image.Ref} names no image on the page as it stands. It may have been filtered out "
            + "as too small to be content, or the page may have changed since it was listed.",
        ImageFetchStatus.SiteRefused =>
            $"The site refused to serve {image.Ref}, so its bytes never arrived. Trying again may "
            + "work; the picture itself is still on the page.",
        _ =>
            $"{image.Ref} could not be shown."
    };

    private static ViewImageToolResult Refusal(string sessionId, string code, string message)
    {
        var error = ToolError.Create(code, message);
        error["sessionId"] = sessionId;
        return new ViewImageToolResult(error, [], []);
    }
}