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
                                         A snippet is the search engine's cached summary, written at crawl time — it can be
                                         stale or wrong, and the page it points at wins where they disagree. Use snippets to
                                         choose which result to open, never as the source of a factual answer: open the page
                                         with web_browse and answer from what it says.
                                         """;

    protected async Task<JsonNode> RunAsync(
        string query,
        int maxResults,
        string? site,
        DateRange? dateRange,
        CancellationToken ct)
    {
        // Some models open a turn with a search for the literal word "noop" and then do the real
        // work — a throat-clearing probe, not an information need. Answering it here keeps the
        // billed search API and its latency out of a turn that never wanted the web; the eval's
        // checks ignore the same query by the same definition. See
        // .scratch/findings-from-the-eval/issues/05-a-turn-sometimes-opens-with-a-noop-search.md.
        if (string.Equals(query.Trim(), "noop", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["status"] = "noop",
                ["query"] = query,
                ["totalResults"] = 0,
                ["results"] = new JsonArray(),
                ["message"] = "No search was performed: 'noop' asks for nothing. Call web_search "
                              + "only with a real information need; continue with the user's request."
            };
        }

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
            // Where the model reads it: at the moment the snippet tempts it to answer. The same
            // rule as prompt prose was tried there and measurably changed nothing — see
            // .scratch/findings-from-the-eval/issues/03-an-answer-comes-from-the-snippet.md.
            ["note"] = "Snippets are cached summaries and may be stale; to answer a factual "
                       + "question, open the page with web_browse and answer from what it says.",
            ["query"] = result.Query,
            ["totalResults"] = result.TotalResults,
            ["results"] = resultsArray,
            ["searchEngine"] = result.SearchEngine,
            ["searchTime"] = result.SearchTimeSeconds
        };
    }

}