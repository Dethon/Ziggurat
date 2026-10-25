using System.ComponentModel;
using Domain.Channels;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebLoginTool(IdentityMap identities, ILoginHandoffs handoffs)
    : WebLoginTool(identities, handoffs)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The site's login page, or any page of the site that leads to it (http or https)")]
        string url,
        CancellationToken ct = default) =>
        ToolResponse.Create(await RunAsync(ConversationScope.Parse(context.Params?.Meta), url, ct));
}