using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public record WebBrowseToolResult(JsonNode Envelope, string? Body, string? Snapshot = null);

public class WebBrowseTool(IWebBrowser browser)
{
    public const string Name = "web_browse";

    // What the tool does and what comes back; which parameter narrows what is on the parameters,
    // and when to reach for each is the web-browsing skill's.
    protected const string Description =
        """
        Navigates to an http or https URL and returns the page as markdown, with its JSON-LD when
        the page has any. Not a file reader: file:// and filesystem paths are refused. The browser
        session persists across calls (cookies, logins), and cookie, age and newsletter popups are
        dismissed on the way in.
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

        // The server's own answer comes first: a 404 renders as an empty document and would
        // otherwise be a "success" with no title and no content, which read as an empty page —
        // and a model that had guessed the url went on to guess the next one.
        var envelope = new JsonObject
        {
            ["status"] = result switch
            {
                { HttpStatus: 404 } => "not_found",
                { HttpStatus: >= 400 } => "http_error",
                { Status: BrowseStatus.CaptchaRequired } => "captcha_required",
                { Status: BrowseStatus.Partial } => "partial",
                _ => "success"
            },
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["contentLength"] = result.ContentLength,
            ["truncated"] = result.Truncated
        };

        if (result.HttpStatus is { } httpStatus)
        {
            envelope["httpStatus"] = httpStatus;
        }

        if (result.HttpStatus is 404)
        {
            envelope["hint"] = "The server has no page at this url. Do not try another spelling of "
                               + "it: take the link from a page you have read or from a search result.";
        }
        else if (result.HttpStatus is >= 400)
        {
            envelope["hint"] = "The server answered with an error for this url; what loaded, if "
                               + "anything, is its error page and not the content.";
        }

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
        if (result.NextOffset is { } nextOffset)
        {
            envelope["nextOffset"] = nextOffset;
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