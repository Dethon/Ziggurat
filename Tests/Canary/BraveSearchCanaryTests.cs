using Domain.Contracts;
using Infrastructure.Clients;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Canary;

// The other of the suite's two calls to a real paid API: one Brave search, so a change to the API's
// shape fails a routine run before it fails the deployment. The query's filters and parsing are
// pinned against a canned answer in Tests/Unit/Infrastructure/BraveSearchClientTests.cs; this only
// asks whether the real API still answers that way. Skips on a machine with no key.
[Trait("Category", "Canary")]
public class BraveSearchCanaryTests
{
    private readonly string? _apiKey;

    public BraveSearchCanaryTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<BraveSearchCanaryTests>()
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