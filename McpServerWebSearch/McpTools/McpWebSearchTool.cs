using System.ComponentModel;
using System.Net;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

// Searched on the calling household's own Brave key, read through the call's household
// (call.Household): the server holds none, so a household that set none searches nothing rather than
// on somebody else's.
[McpServerToolType]
public class McpWebSearchTool(BraveSearchClients brave) : WebSearchTool
{
    [McpServerTool(Name = Name, ReadOnly = true)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> call,
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
        var result = await RunAsync(new OnTheHouseholdsKey(call.Household, brave), query, maxResults, site, dateRange, ct);
        return ToolResponse.Create(result);
    }

    // Reads the key only when something is really searched: a noop query is answered with none.
    private sealed class OnTheHouseholdsKey(ICallHousehold? household, BraveSearchClients brave) : IWebSearchClient
    {
        public async Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken ct = default)
        {
            if (household is null)
            {
                throw new UnauthorizedAccessException(
                    "This search acts for no household, and the web is searched on a household's own Brave key.");
            }

            var key = await household.BraveSearchKeyAsync(ct)
                      ?? throw new HttpRequestException(
                          "This household has set no Brave Search key, so the web cannot be searched. An owner of the household adds one on the Keys page.",
                          null, HttpStatusCode.Unauthorized);
            return await brave.For(key).SearchAsync(query, ct);
        }
    }
}