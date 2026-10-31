using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Files;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileSearchTool(
    ISearchClient client,
    ISearchResultsManager searchResultsManager) : FileSearchTool(client, searchResultsManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string[] searchStrings,
        CancellationToken cancellationToken)
    {
        if (!context.TryScope(out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope search results."));
        }

        return ToolResponse.Create(await Run(sessionId, searchStrings, cancellationToken));
    }
}