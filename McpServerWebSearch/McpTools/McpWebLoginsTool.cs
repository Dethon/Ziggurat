using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebLoginsTool(ILoginsPages pages, ILoginHandoffs handoffs)
    : WebLoginsTool(pages, handoffs)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(RequestContext<CallToolRequestParams> context, CancellationToken ct = default) =>
        ToolResponse.Create(await RunAsync(context.Turn, ct));
}