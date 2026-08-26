using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public record WebBrowseToolResult(JsonNode Envelope, string? Body, string? Snapshot = null);

public class WebBrowseTool(IWebBrowser browser)
{
    public const string Name = "web_browse";

    protected const string Description =
        """
        Navigates to a URL and returns page content as markdown.
        Loads web pages only — http and https. It cannot read local files or directories:
        file:// URLs are refused, and a filesystem path is not a URL.
        Maintains a persistent browser session (cookies, login state preserved).
        Automatically dismisses cookie popups, age gates, newsletter modals.

        Use selector to extract specific elements (e.g., selector=".product-card").
        Use maxLength/offset for pagination of long content.
        Use useReadability=true for clean article extraction (strips ads, nav, sidebars).
        Use scrollToLoad=true for pages with lazy-loaded content.
        Use snapshot=true to include the accessibility tree in the same call when you intend
        to interact with the page (saves a follow-up web_snapshot round trip).

        Returns structured data (JSON-LD) when available on the page.

        For interacting with pages (clicking, filling forms), use snapshot=true (or web_snapshot)
        and then web_action.
        """;

    protected async Task<WebBrowseToolResult> RunAsync(
        string sessionId,
        string url,
        string? selector,
        int maxLength,
        int offset,
        bool useReadability,
        bool scrollToLoad,
        int scrollSteps,
        bool snapshot,
        CancellationToken ct)
    {
        maxLength = Math.Clamp(maxLength, 100, 100000);
        offset = Math.Max(0, offset);
        scrollSteps = Math.Clamp(scrollSteps, 1, 10);

        var request = new BrowseRequest(
            SessionId: sessionId,
            Url: url,
            Selector: selector,
            MaxLength: maxLength,
            Offset: offset,
            UseReadability: useReadability,
            ScrollToLoad: scrollToLoad,
            ScrollSteps: scrollSteps);

        var result = await browser.NavigateAsync(request, ct);

        if (result.Status is BrowseStatus.Error or BrowseStatus.SessionNotFound)
        {
            var (code, hint) = result.Status switch
            {
                BrowseStatus.SessionNotFound => (
                    ToolError.Codes.SessionNotFound,
                    "The browser session has expired. Call web_browse again with a fresh sessionId."),
                _ => (ToolError.Codes.InternalError, (string?)null)
            };
            var error = ToolError.Create(
                code,
                result.ErrorMessage ?? "Browse failed",
                hint);
            error["sessionId"] = result.SessionId;
            error["url"] = result.Url;
            return new WebBrowseToolResult(error, null);
        }

        var envelope = new JsonObject
        {
            ["status"] = result.Status switch
            {
                BrowseStatus.CaptchaRequired => "captcha_required",
                BrowseStatus.Partial => "partial",
                _ => "success"
            },
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["contentLength"] = result.ContentLength,
            ["truncated"] = result.Truncated
        };

        if (result.ImageCount > 0)
        {
            envelope["imageCount"] = result.ImageCount;
        }

        // Only worth saying when it changes what the model would do next.
        if (result.ImagesBeyondWindow > 0)
        {
            envelope["imagesBeyondWindow"] = result.ImagesBeyondWindow;
        }

        // Truncation backs the cut up — past a partial image entry, or to a newline — so the
        // window ends short of offset + maxLength, and paging by maxLength would skip what the
        // back-up left. The envelope names the exact continuation instead.
        if (result.Truncated && result.Content is { Length: > 0 })
        {
            envelope["nextOffset"] = offset + result.Content.Length;
        }

        if (result.Metadata is not null)
        {
            envelope["metadata"] = new JsonObject
            {
                ["description"] = result.Metadata.Description,
                ["author"] = result.Metadata.Author,
                ["datePublished"] = result.Metadata.DatePublished?.ToString("yyyy-MM-dd"),
                ["siteName"] = result.Metadata.SiteName
            };
        }

        if (result.StructuredData is { Count: > 0 })
        {
            var sdArray = new JsonArray();
            foreach (var sd in result.StructuredData)
            {
                sdArray.Add(new JsonObject
                {
                    ["type"] = sd.Type,
                    ["data"] = sd.RawJson
                });
            }
            envelope["structuredData"] = sdArray;
        }

        if (result.DismissedModals is { Count: > 0 })
        {
            var modals = new JsonArray();
            foreach (var m in result.DismissedModals)
            {
                modals.Add(m.Type.ToString());
            }

            envelope["dismissedModals"] = modals;
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            envelope["message"] = result.ErrorMessage;
        }

        string? snapshotBody = null;
        if (snapshot && result.Status is BrowseStatus.Success or BrowseStatus.Partial)
        {
            // Named to the page this browse landed on: a parallel browse may have moved the
            // session's current tab, and its refs on this page's text would be the silent
            // wrong-target answer.
            var snapshotResult = await browser.SnapshotAsync(
                new SnapshotRequest(sessionId, selector, ForUrl: result.Url), ct);
            if (snapshotResult.ErrorMessage is null)
            {
                envelope["refCount"] = snapshotResult.RefCount;
                snapshotBody = snapshotResult.Snapshot ?? string.Empty;
            }
            else
            {
                envelope["snapshotError"] = snapshotResult.ErrorMessage;
            }
        }

        return new WebBrowseToolResult(envelope, result.Content ?? string.Empty, snapshotBody);
    }
}