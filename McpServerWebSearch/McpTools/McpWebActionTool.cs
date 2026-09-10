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
public class McpWebActionTool(IWebBrowser browser)
    : WebActionTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Element ref from a snapshot or a diff; required for every action but back")]
        string? @ref = null,
        [Description("Action to perform on the element")]
        WebActionType action = WebActionType.Click,
        [Description("Value: text to type/fill, option text for select, key name for press (Enter/Tab/Escape/ArrowDown)")]
        string? value = null,
        [Description("Target ref for drag action (drag from ref to endRef)")]
        string? endRef = null,
        [Description("Wait for page navigation after action (for clicks that load new pages)")]
        bool waitForNavigation = false,
        [Description("Only on a retry of a click that returned Timeout: skips the visible/stable/enabled/unobscured checks and clicks the ref directly. Never on a first attempt.")]
        bool force = false,
        CancellationToken ct = default)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        var result = await ExecuteAsync(sessionId, @ref, action, value, endRef, waitForNavigation, force, ct);
        return ToolResponse.Create(ToJson(result));
    }
}