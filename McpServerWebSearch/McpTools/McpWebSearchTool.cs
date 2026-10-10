using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebSearchTool(IWebSearchClient searchClient)
    : WebSearchTool(searchClient)
{
    [McpServerTool(Name = Name, ReadOnly = true)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("The search query. Be specific for better results.")]
        string query,
        [Description("Maximum results to return (1-20, default: 10)")]
        int maxResults = 10,
        [Description("Limit search to specific domain (e.g., 'imdb.com', 'wikipedia.org')")]
        string? site = null,
        [Description("Filter results by recency window")]
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(query, maxResults, site, dateRange, ct);
        return ToolResponse.Create(result);
    }
}