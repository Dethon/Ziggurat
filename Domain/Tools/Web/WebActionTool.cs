using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebActionTool(IWebBrowser browser)
{
    public const string Name = "web_action";

    // What the tool does and what comes back. The verbs are the `action` enum and `value` says
    // what each takes; when to type rather than fill, and when `force` is allowed, are the
    // web-browsing skill's — the essay on the flag lived here and cost every request of every
    // conversation, web or not.
    protected const string Description =
        """
        Acts on one element of the current page by its ref, or navigates back. Returns a diff of
        what changed, with the refs the change added; an action that navigated returns the new
        page's full snapshot instead.
        """;

    protected async Task<WebActionResult> ExecuteAsync(
        string sessionId,
        string? @ref,
        WebActionType action,
        string? value,
        string? endRef,
        bool waitForNavigation,
        bool force,
        CancellationToken ct)
    {
        // A ref's shape says which tool it was meant for, so the other namespace is turned away by
        // name here — the mirror of view_image refusing e-3 — rather than failing to be found.
        if (new[] { @ref, endRef }.Where(ImageRef.IsImageRef).ToList() is { Count: > 0 } foreign)
        {
            return new WebActionResult(
                sessionId, WebActionStatus.NotAnElementRef, null, false, null, null,
                $"{string.Join(", ", foreign)} "
                + $"{(foreign.Count == 1 ? "is not an element ref" : "are not element refs")}. "
                + "Element refs look like e-1 and come from web_snapshot; "
                + "i-style refs name pictures, and view_image is what looks at those.");
        }

        var request = new WebActionRequest(
            SessionId: sessionId,
            Ref: @ref,
            Action: action,
            Value: value,
            EndRef: endRef,
            WaitForNavigation: waitForNavigation,
            Force: force);

        return await browser.ActionAsync(request, ct);
    }

    protected static JsonNode ToJson(WebActionResult result)
    {
        if (result.Status is not WebActionStatus.Success)
        {
            // The two stale-ref walls each name their recovery: a superseded ref's page is still
            // open and refreshing it mints fresh refs; a closed ref's wall names exactly what to
            // browse again.
            var (code, hint, wall) = result.Status switch
            {
                WebActionStatus.SessionNotFound => (
                    ToolError.Codes.SessionNotFound,
                    "The browser session has expired. Call web_browse to start a new session.",
                    (string?)null),
                WebActionStatus.NotAnElementRef => (
                    ToolError.Codes.InvalidArgument,
                    "Act on the element's e- ref; to look at the picture, call view_image with its i- ref.",
                    null),
                WebActionStatus.ElementNotFound => (
                    ToolError.Codes.ElementNotFound,
                    "Call web_snapshot to refresh element refs — the page or DOM may have changed.",
                    null),
                WebActionStatus.Timeout => (
                    ToolError.Codes.Timeout,
                    "Element may be obscured by an overlay. Retry once with force=true if you're certain the ref is correct.",
                    null),
                // A tab that navigated away from the ref's page is one step past it, and the way
                // back to those refs is the browser's back — "browse it again" here sent a model
                // to the second browse the back action exists to avoid.
                WebActionStatus.RefSuperseded when result.Url is { } current
                                                   && !string.Equals(current, result.RefUrl, StringComparison.Ordinal) => (
                    ToolError.Codes.ElementNotFound,
                    $"To act on {result.RefUrl} again, go back to it with web_action action 'back': "
                    + "it returns that page's fresh refs. Do not browse it anew.",
                    $"That ref belonged to {result.RefUrl}; the tab has since navigated to {current}, "
                    + "which renumbered its refs."),
                WebActionStatus.RefSuperseded => (
                    ToolError.Codes.ElementNotFound,
                    $"Call web_snapshot, or web_browse {result.RefUrl} again, and act with the fresh refs.",
                    $"That ref is out of date: {result.RefUrl} has moved on since it was stamped, "
                    + "renumbering its refs."),
                WebActionStatus.RefClosed => (
                    ToolError.Codes.NotFound,
                    $"Browse {result.RefUrl} again and act with the refs it lists.",
                    $"That ref belonged to {result.RefUrl}, whose tab has since been closed."),
                _ => (ToolError.Codes.InternalError, null, null)
            };
            var error = ToolError.Create(
                code,
                wall ?? result.ErrorMessage ?? "Action failed",
                hint);
            error["sessionId"] = result.SessionId;
            error["url"] = result.Url;
            return error;
        }

        var response = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["navigationOccurred"] = result.NavigationOccurred
        };

        if (result.Snapshot is not null)
        {
            response["snapshot"] = result.Snapshot;
        }

        if (result.DialogMessage is not null)
        {
            response["dialogMessage"] = result.DialogMessage;
        }

        if (result.NavigationOccurred)
        {
            response["nextStep"] =
                $"Navigated to {result.Url}. The snapshot above shows interactive refs only, not page text. " +
                "If you need to read article/product/listing content, call web_browse with this URL. " +
                "If you only need to interact further, use the refs in the snapshot with web_action.";
        }

        return response;
    }

}