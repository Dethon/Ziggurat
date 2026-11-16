using Domain.Contracts;
using Infrastructure.Clients;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Integration.Clients;

[Trait("Category", "External")]
public class BraveSearchClientTests : IAsyncLifetime
{
    private readonly string? _apiKey;

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    public BraveSearchClientTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<BraveSearchClientTests>()
            .AddEnvironmentVariables()
            .Build();

        _apiKey = config["BraveSearch:ApiKey"];
    }

    private bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    // The key's plan answers a second request within its window with 429, and two suites running at
    // once share it: that is the API saying "later", not the client failing, so a refused request is
    // asked again after the window rather than read as a defect.
    private static async Task<WebSearchResult> SearchWithinTheRateLimitAsync(BraveSearchClient client, WebSearchQuery query)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.SearchAsync(query);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt));
            }
        }
    }

    [SkippableFact]
    public async Task SearchAsync_WithRealApi_ReturnsResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var client = new BraveSearchClient(Brave(), _apiKey!);

        // Act
        var query = new WebSearchQuery("Dune movie 2024", MaxResults: 5);
        var result = await SearchWithinTheRateLimitAsync(client, query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
        result.SearchEngine.ShouldBe("brave");
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Title));
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Url));
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Domain));
    }

    [SkippableFact]
    public async Task SearchAsync_WithSiteFilter_ReturnsFilteredResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var client = new BraveSearchClient(Brave(), _apiKey!);

        // Act
        var query = new WebSearchQuery("Oppenheimer", MaxResults: 5, Site: "imdb.com");
        var result = await SearchWithinTheRateLimitAsync(client, query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
        result.Results.ShouldAllBe(r => r.Domain.Contains("imdb.com"));
    }

    [SkippableFact]
    public async Task SearchAsync_WithDateRange_ReturnsRecentResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var client = new BraveSearchClient(Brave(), _apiKey!);

        // Act
        var query = new WebSearchQuery(
            "technology news",
            MaxResults: 5,
            DateRange: DateRange.Week);
        var result = await SearchWithinTheRateLimitAsync(client, query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
    }

    // HttpClient's default timeout is a hundred seconds, and a run where Brave stalled spent every
    // one of them — on a suite that finishes in under a minute, one hung third party was the
    // longest thing in it. Ten seconds is far past a healthy answer and short enough that a stall
    // costs a failure rather than the run.
    private static HttpClient Brave() => new()
    {
        BaseAddress = new Uri("https://api.search.brave.com/res/v1/"),
        Timeout = TimeSpan.FromSeconds(10)
    };

}