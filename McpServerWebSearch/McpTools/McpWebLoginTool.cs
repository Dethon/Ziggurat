using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebLoginTool(ILoginHandoffs handoffs)
    : WebLoginTool(handoffs)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The site's login page, or any page of the site that leads to it (http or https)")]
        string url,
        CancellationToken ct = default) =>
        ToolResponse.Create(await RunAsync(context.Turn, url, context.Delegation, ct));
}