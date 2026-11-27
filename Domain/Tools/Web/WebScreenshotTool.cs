using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.Web;

// The call's envelope, and the picture when one was taken. The picture rides the view_image wire
// unchanged -- its own PageImageResult envelope, then its bytes -- so the bridge lifts it and
// hydration keys and forgets it exactly like a page image.
public record WebScreenshotToolResult(JsonNode Envelope, ViewedImage? Image);

public class WebScreenshotTool(IWebBrowser browser)
{
    public const string Name = "web_screenshot";

    // What the tool does and what comes back; when to reach for it rather than the page text or a
    // snapshot is the web-browsing skill's.
    protected const string Description =
        """
        Shows the current page as a picture, as a person would see it: what is on screen now, or
        with `screen` the Nth screen-high slice from the top. Says which screen it shows and how
        many the page has.
        """;

    protected async Task<WebScreenshotToolResult> RunAsync(string sessionId, int? screen, CancellationToken ct)
    {
        if (screen < 1)
        {
            return Refusal(sessionId, ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Screens are counted from 1 at the top of the page. Omit screen to see the page where it stands."));
        }

        var result = await browser.ScreenshotAsync(new ScreenshotRequest(sessionId, screen), ct);

        if (result is not { Status: ScreenshotStatus.Success, Bytes: { } bytes, MediaType: { } mediaType })
        {
            return Refusal(sessionId, result.Status switch
            {
                ScreenshotStatus.SessionNotFound => ToolError.Create(
                    ToolError.Codes.SessionNotFound,
                    "No page is open in this conversation's browser.",
                    "Call web_browse to open the page first."),
                ScreenshotStatus.TabClosed => ToolError.Create(
                    ToolError.Codes.NotFound,
                    $"The page last in front, {result.Url}, has since been closed.",
                    $"Browse {result.Url} again, then take the screenshot."),
                _ => ToolError.Create(
                    ToolError.Codes.InternalError,
                    result.ErrorMessage ?? "The screenshot failed.")
            });
        }

        var page = string.IsNullOrWhiteSpace(result.Title) ? result.Url : result.Title.Trim();
        var envelope = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = sessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["screen"] = result.Screen,
            ["screens"] = result.Screens
        };

        var image = new ViewedImage(
            "screenshot",
            mediaType,
            bytes,
            FsResultContract.ToNode(new PageImageResult
            {
                ImageRef = "screenshot",
                // The name a note left in the picture's place goes by, once its bytes are gone.
                Label = $"screenshot of {page}, screen {result.Screen} of {result.Screens}",
                MediaType = mediaType,
                SizeBytes = bytes.Length,
                Shown = true,
                Screenshot = true
            }));

        return new WebScreenshotToolResult(envelope, image);
    }

    private static WebScreenshotToolResult Refusal(string sessionId, JsonObject error)
    {
        error["sessionId"] = sessionId;
        return new WebScreenshotToolResult(error, null);
    }
}