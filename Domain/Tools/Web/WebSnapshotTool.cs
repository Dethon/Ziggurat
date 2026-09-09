using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public record WebSnapshotToolResult(JsonNode Envelope, string? Body);

public class WebSnapshotTool(IWebBrowser browser)
{
    public const string Name = "web_snapshot";

    protected const string Description =
        """
        Returns the current page's accessibility tree: every element with its state, and a ref
        on each interactive one for web_action.
        """;

    protected async Task<WebSnapshotToolResult> RunAsync(
        string sessionId,
        string? selector,
        CancellationToken ct)
    {
        var request = new SnapshotRequest(sessionId, selector);
        var result = await browser.SnapshotAsync(request, ct);

        if (result.ErrorMessage is not null)
        {
            var error = ToolError.Create(
                ToolError.Codes.InternalError,
                result.ErrorMessage);
            error["sessionId"] = result.SessionId;
            return new WebSnapshotToolResult(error, null);
        }

        var envelope = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["refCount"] = result.RefCount
        };

        return new WebSnapshotToolResult(envelope, result.Snapshot ?? string.Empty);
    }
}