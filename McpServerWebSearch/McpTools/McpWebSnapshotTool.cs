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
public class McpWebSnapshotTool(IWebBrowser browser)
    : WebSnapshotTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("CSS selector to limit snapshot scope (e.g. 'main', '.search-form'). Omit for full page.")]
        string? selector = null,
        CancellationToken ct = default)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        var result = await RunAsync(sessionId, selector, ct);
        return result.Body is null
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, result.Body);
    }
}