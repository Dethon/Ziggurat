using Domain.Contracts;
using Infrastructure.HtmlProcessing;

namespace Infrastructure.Clients.Browser;

// The ref located on its page: the strings the page offers about the picture (for the one label
// ladder), the address the src resolved to, and whether that address shares the page's origin.
// Null Url means no URL parser accepts the src -- nothing below the locate can run.
internal record LocatedImage(ImageLabelFacts Facts, string? Url, bool SameOrigin);

// What a fetch attempt observed, verbatim: whether the response was OK, the content-type header
// as sent, the body. The acceptance rule lives in the descent, not here, so the wire rung and the
// context-request rung cannot judge the same header differently.
internal record WireRungAnswer(bool Ok, string? MediaType, byte[]? Bytes);

internal enum CanvasOutcome
{
    // The pixels came off the canvas as PNG.
    Drawn,

    // The browser shows the image but will not let script read it -- the pixels are on screen,
    // so the descent continues rather than refusing.
    Tainted,

    // The bytes never arrived at all: a dead link, not a CDN guarding its pixels.
    NeverLoaded,

    // The canvas failed for some other reason; the browser itself will not show the picture.
    Failed
}

internal record CanvasRungAnswer(CanvasOutcome Outcome, byte[]? Png);

// One method per rung of the byte-acquisition descent, each an observation with no policy in it.
// The Playwright-backed probe is the only production implementation; a test fakes it and the
// whole descent runs in milliseconds without a browser.
internal interface IImagePageProbe
{
    Task<LocatedImage?> LocateAsync(string imageRef);
    Task<WireRungAnswer?> WireFetchAsync(string url, bool withCredentials);
    Task<CanvasRungAnswer> CanvasReadAsync(string imageRef, string url);
    Task<WireRungAnswer?> ContextRequestAsync(string url);
    Task<byte[]?> ElementScreenshotAsync(string imageRef);
}

// The descent itself: wire fetch, canvas read, context request, element screenshot, each step
// down trading fidelity for reach, every step decided here. The whole sequence runs inside one
// locked tab work callback, so the page cannot change between rungs.
internal static class ImageAcquisition
{
    // The rasters the chat wire accepts. Anything else leaves as pixels: as-served SVG bytes made
    // the vision provider refuse the whole request with HTTP 400. One constant, one comparison --
    // AcceptedAsServed is the only reader.
    private static readonly string[] WireRasters =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static async Task<FetchedImage> FetchAsync(IImagePageProbe probe, string imageRef)
    {
        var located = await probe.LocateAsync(imageRef);
        if (located is null)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.NotAnImageRef);
        }

        var label = ImageLabel.From(located.Facts);
        if (located.Url is null)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
        }

        // Same-origin sends the page's own credentials; cross-origin goes anonymous, because the
        // big image CDNs answer ACAO * and a wildcard is exactly what a credentialed request is
        // rejected against. This broke the first implementation; do not "fix" it.
        var wire = await probe.WireFetchAsync(located.Url, withCredentials: located.SameOrigin);

        // A wire answer with no content-type at all has always been taken at the wire's word as a
        // jpeg on this rung, rather than paying the canvas re-encode for a header a lazy CDN
        // omitted. The context-request rung takes no such word: by then the anonymous fetch has
        // already failed, and a nameless answer there falls to the screenshot.
        if (wire is { Ok: true, Bytes: not null }
            && AcceptedAsServed(wire.MediaType ?? "image/jpeg") is { } wireType)
        {
            return new FetchedImage(imageRef, wireType, wire.Bytes, ImageFetchStatus.Success, Label: label);
        }

        var canvas = await probe.CanvasReadAsync(imageRef, located.Url);
        return canvas switch
        {
            { Outcome: CanvasOutcome.Drawn, Png: not null } => new FetchedImage(
                imageRef, "image/png", canvas.Png, ImageFetchStatus.Success, Label: label),
            { Outcome: CanvasOutcome.NeverLoaded } => new FetchedImage(
                imageRef, null, null, ImageFetchStatus.NeverLoaded),
            { Outcome: CanvasOutcome.Tainted } => await PastTheTaintAsync(probe, imageRef, located.Url, label),
            _ => new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused)
        };
    }

    // A tainted canvas is not a refusal but two more rungs: CORS binds script inside the page and
    // nothing else, so the context's request client pulls the same address with the same cookies
    // and keeps the bytes as served; failing that, a screenshot of the element reads the pixels
    // the compositor already painted.
    private static async Task<FetchedImage> PastTheTaintAsync(
        IImagePageProbe probe, string imageRef, string url, string label)
    {
        var context = await probe.ContextRequestAsync(url);
        if (context is { Ok: true, Bytes: not null }
            && AcceptedAsServed(context.MediaType) is { } contextType)
        {
            return new FetchedImage(
                imageRef, contextType, context.Bytes, ImageFetchStatus.Success, Label: label);
        }

        var shot = await probe.ElementScreenshotAsync(imageRef);
        return shot is null
            ? new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused)
            : new FetchedImage(imageRef, "image/png", shot, ImageFetchStatus.Success, Label: label);
    }

    // The one place a content-type header is read: parameters dropped, whitespace trimmed, then
    // judged against the one raster set. Returns the normalized type, or null for anything that
    // may not leave as served.
    private static string? AcceptedAsServed(string? mediaType)
    {
        var trimmed = mediaType?.Split(';')[0].Trim();
        return trimmed is not null && WireRasters.Contains(trimmed) ? trimmed : null;
    }
}