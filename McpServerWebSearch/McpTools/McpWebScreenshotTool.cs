using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebScreenshotTool(IWebBrowser browser) : WebScreenshotTool(browser)
{
    // The whole session: the picture is of the current tab, which only the server knows, and a
    // screen asked for scrolls it.
    [McpServerTool(Name = Name)]
    [McpMeta(FootprintTemplate.MetaKey, JsonValue = BrowserFootprint.Session)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Which screen-high slice of the page to show, 1 for the top; omit to show what is on screen now")]
        int? screen = null,
        CancellationToken ct = default)
    {
        if (!context.TryScope(out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        // Whether the model can be shown it is the agent's question, as for view_image: hydration
        // answers a vision-less turn with a note.
        var result = await RunAsync(sessionId, screen, ct);
        return result.Image is null
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, [result.Image]);
    }
}