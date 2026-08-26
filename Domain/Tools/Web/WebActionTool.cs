using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebActionTool(IWebBrowser browser)
{
    public const string Name = "web_action";

    protected const string Description =
        """
        Interacts with an element on the current page by ref from web_snapshot.
        Returns a diff showing only what changed — unless the action caused navigation,
        in which case the full new page snapshot is returned instead.
        Use web_snapshot with a selector if you need more context after a diff.

        Actions requiring ref:
        - 'click': Click the element
        - 'type': Type character-by-character (triggers autocomplete). Set value to text.
        - 'fill': Set input value directly (no keystroke events). Set value to text.
        - 'select': Select native dropdown option. Set value to option text.
        - 'press': Press keyboard key. Set value to key name (Enter, Tab, Escape, ArrowDown).
        - 'clear': Clear input field.
        - 'hover': Hover over element (triggers tooltips, menus).
        - 'focus': Focus element (triggers datepickers, dropdowns that open on focus).
        - 'drag': Drag element to target. Set endRef to destination element ref.

        Actions NOT requiring ref (return full snapshot):
        - 'back': Navigate back in browser history.

        Workflow: web_snapshot -> find ref -> web_action(ref, action) -> read snapshot in response.
        For autocomplete: type partial text -> response shows options -> click option ref.

        force: only set this on a click that returned 'Timeout'. By default, clicks wait until the
        element is visible, stable, enabled, and not obscured by another element. Some pages layer
        a non-semantic <label>, decorative overlay, or floating placeholder over an input — those
        elements have no ARIA role, so they don't appear in the web_snapshot but they intercept
        hit-testing and the click hangs until timeout. force=true skips those checks and dispatches
        the click directly on the target ref. Do NOT set force on the first attempt: the default
        checks are also what catches genuine "wrong ref / element gone / a real modal is in the
        way" bugs, and forcing them silently makes a click land on the wrong thing.
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
                WebActionStatus.ElementNotFound => (
                    ToolError.Codes.ElementNotFound,
                    "Call web_snapshot to refresh element refs — the page or DOM may have changed.",
                    null),
                WebActionStatus.Timeout => (
                    ToolError.Codes.Timeout,
                    "Element may be obscured by an overlay. Retry once with force=true if you're certain the ref is correct.",
                    null),
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