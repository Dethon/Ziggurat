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
public class McpViewImageTool(IWebBrowser browser) : ViewImageTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Image refs from the page text, e.g. [\"i-1\", \"i-4\"]. Up to 8 per call.")]
        string[] refs,
        CancellationToken ct = default)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        // Whether the model can be shown a picture is the agent's question, not this server's: the
        // capability catalogue and the model a turn resolved to both live hub-side. A turn that
        // cannot take images gets its own refusal from hydration, which knows.
        var result = await RunAsync(sessionId, refs ?? [], modelAcceptsImages: true, ct);

        return result.Images.Count == 0
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, result.Images);
    }
}