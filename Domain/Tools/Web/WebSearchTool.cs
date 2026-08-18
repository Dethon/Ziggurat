using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebSearchTool(IWebSearchClient searchClient)
{
    public const string Name = "web_search";

    protected const string Description = """
                                         Searches the web and returns relevant results with titles, snippets, and URLs.
                                         Use this to find current information about movies, TV shows, music, news, documentation, or any other topic.
                                         Results include title, URL, snippet, domain, and publication date when available.
                                         """;

    protected async Task<JsonNode> RunAsync(
        string query,
        int maxResults,
        string? site,
        DateRange? dateRange,
        CancellationToken ct)
    {
        var searchQuery = new WebSearchQuery(
            Query: query,
            MaxResults: Math.Clamp(maxResults, 1, 20),
            Site: site,
            DateRange: dateRange
        );

        var result = await searchClient.SearchAsync(searchQuery, ct);

        if (result.Results.Count == 0)
        {
            return new JsonObject
            {
                ["status"] = "no_results",
                ["query"] = result.Query,
                ["totalResults"] = 0,
                ["results"] = new JsonArray(),
                ["suggestion"] = "No results found. Try broader search terms or check spelling."
            };
        }

        var resultsArray = new JsonArray();
        foreach (var item in result.Results)
        {
            resultsArray.Add(new JsonObject
            {
                ["title"] = item.Title,
                ["url"] = item.Url,
                ["snippet"] = item.Snippet,
                ["domain"] = item.Domain,
                ["datePublished"] = item.DatePublished?.ToString("yyyy-MM-dd")
            });
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["query"] = result.Query,
            ["totalResults"] = result.TotalResults,
            ["results"] = resultsArray,
            ["searchEngine"] = result.SearchEngine,
            ["searchTime"] = result.SearchTimeSeconds
        };
    }

}