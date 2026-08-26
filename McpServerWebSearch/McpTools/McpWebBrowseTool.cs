using System.ComponentModel;
using Domain.Channels;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebBrowseTool(IWebBrowser browser)
    : WebBrowseTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The URL to navigate to")]
        string url,
        [Description("Maximum characters to return (100-100000, default: 10000)")]
        int maxLength = 10000,
        [Description("Character offset to start from (0 for the beginning; to page forward, pass the nextOffset the previous call returned)")]
        int offset = 0,
        [Description("CSS selector to extract specific elements (e.g. '.product-card', '#main'). Returns ALL matches.")]
        string? selector = null,
        [Description("Extract clean article content, stripping ads, navigation, sidebars")]
        bool useReadability = false,
        [Description("Scroll page to trigger lazy-loaded content")]
        bool scrollToLoad = false,
        [Description("Number of scroll steps for lazy loading (1-10, default: 3)")]
        int scrollSteps = 3,
        [Description("Include the accessibility tree (snapshot) in the same call. Use when you intend to interact with the page; saves a separate web_snapshot round trip.")]
        bool snapshot = false,
        CancellationToken ct = default)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        var result = await RunAsync(sessionId, url, selector, maxLength, offset,
            useReadability, scrollToLoad, scrollSteps, snapshot, ct);
        return result.Body is null
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, result.Body, result.Snapshot);
    }
}