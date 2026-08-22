using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Web;
using Domain.Contracts;
using JetBrains.Annotations;

namespace Infrastructure.Clients;

public class BraveSearchClient(HttpClient httpClient, string apiKey) : IWebSearchClient
{
    private const string SearchEndpoint = "web/search";

    public async Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken ct = default)
    {
        var url = BuildSearchUrl(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Add("Accept", "application/json");

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var braveResponse = await response.Content.ReadFromJsonAsync<BraveSearchResponse>(ct)
                            ?? throw new InvalidOperationException("Failed to deserialize Brave Search response");

        return MapToWebSearchResult(query.Query, braveResponse);
    }

    private static string BuildSearchUrl(WebSearchQuery query)
    {
        var queryParams = HttpUtility.ParseQueryString(string.Empty);
        queryParams["q"] = query.Query;
        queryParams["count"] = Math.Min(query.MaxResults, 20).ToString();

        if (!string.IsNullOrEmpty(query.Site))
        {
            queryParams["q"] = $"site:{query.Site} {query.Query}";
        }

        if (query.DateRange.HasValue)
        {
            queryParams["freshness"] = query.DateRange.Value switch
            {
                DateRange.Day => "pd",
                DateRange.Week => "pw",
                DateRange.Month => "pm",
                DateRange.Year => "py",
                _ => throw new ArgumentOutOfRangeException(nameof(query.DateRange))
            };
        }

        queryParams["safesearch"] = "off";

        return $"{SearchEndpoint}?{queryParams}";
    }

    private static WebSearchResult MapToWebSearchResult(string query, BraveSearchResponse response)
    {
        var results = response.Web?.Results ?? [];
        var items = results.Select(r => new WebSearchResultItem(
            Title: r.Title ?? string.Empty,
            Url: r.Url ?? string.Empty,
            Snippet: TruncateSnippet(r.Description ?? string.Empty, 200),
            Domain: ExtractDomain(r.Url ?? string.Empty),
            DatePublished: ParseDate(r.PageAge)
        )).ToList();

        return new WebSearchResult(
            Query: query,
            TotalResults: response.Web?.TotalResults ?? items.Count,
            Results: items,
            SearchEngine: "brave",
            SearchTimeSeconds: response.Query?.ResponseTime ?? 0
        );
    }

    private static string TruncateSnippet(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        var truncated = text[..(maxLength - 3)];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > maxLength / 2)
        {
            truncated = truncated[..lastSpace];
        }

        return truncated + "...";
    }

    private static string ExtractDomain(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        try
        {
            var uri = new Uri(url);
            return uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateOnly? ParseDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
        {
            return null;
        }

        if (DateTime.TryParse(dateString, out var date))
        {
            return DateOnly.FromDateTime(date);
        }

        return null;
    }

    private record BraveSearchResponse
    {
        [JsonPropertyName("query")] public BraveQuery? Query { get; init; }

        [JsonPropertyName("web")] public BraveWebResults? Web { get; init; }
    }

    private record BraveQuery
    {
        [UsedImplicitly]
        [JsonPropertyName("response_time")]
        public double ResponseTime { get; init; }
    }

    private record BraveWebResults
    {
        [UsedImplicitly]
        [JsonPropertyName("results")]
        public List<BraveWebResult>? Results { get; init; }

        // Nullable on purpose: the live API omits "total_results" entirely, and a non-nullable
        // long turned that absence into a real 0, which then beat the "?? items.Count" fallback
        // and made every successful search report no results.
        [UsedImplicitly]
        [JsonPropertyName("total_results")]
        public long? TotalResults { get; init; }
    }

    [UsedImplicitly]
    private record BraveWebResult
    {
        [UsedImplicitly]
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [UsedImplicitly]
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [UsedImplicitly]
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [UsedImplicitly]
        [JsonPropertyName("page_age")]
        public string? PageAge { get; init; }
    }
}