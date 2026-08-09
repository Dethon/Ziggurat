using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

public class BraveSearchClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly BraveSearchClient _client;

    public BraveSearchClientTests()
    {
        _server = WireMockServer.Start();
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_server.Url!)
        };
        _client = new BraveSearchClient(httpClient, "test-api-key");
    }

    [Fact]
    public async Task SearchAsync_WithValidQuery_ReturnsResults()
    {
        // Arrange
        var response = new
        {
            query = new { response_time = 0.42 },
            web = new
            {
                total_results = 100,
                results = new[]
                {
                    new
                    {
                        title = "Test Result",
                        url = "https://example.com/page",
                        description = "This is a test result",
                        page_age = "2024-01-15"
                    }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/web/search")
                .WithHeader("X-Subscription-Token", "test-api-key")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var query = new WebSearchQuery("test query");
        var result = await _client.SearchAsync(query);

        // Assert
        result.ShouldNotBeNull();
        result.Query.ShouldBe("test query");
        result.TotalResults.ShouldBe(100);
        result.Results.Count.ShouldBe(1);
        result.Results[0].Title.ShouldBe("Test Result");
        result.Results[0].Url.ShouldBe("https://example.com/page");
        result.Results[0].Domain.ShouldBe("example.com");
        result.SearchEngine.ShouldBe("brave");
    }

    [Fact]
    public async Task SearchAsync_WithSiteFilter_IncludesSiteInQuery()
    {
        // Arrange
        var response = new
        {
            query = new { response_time = 0.1 },
            web = new { total_results = 0, results = Array.Empty<object>() }
        };

        _server.Given(Request.Create()
                .WithPath("/web/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var query = new WebSearchQuery("test", Site: "imdb.com");
        await _client.SearchAsync(query);

        // Assert
        var requests = _server.LogEntries;
        requests.ShouldNotBeEmpty();
        var firstRequest = requests[0];
        firstRequest.RequestMessage?.Query?["q"].ToString().ShouldContain("site:imdb.com");
    }

    [Fact]
    public async Task SearchAsync_WithDateRange_IncludesFreshnessParam()
    {
        // Arrange
        var response = new
        {
            query = new { response_time = 0.1 },
            web = new { total_results = 0, results = Array.Empty<object>() }
        };

        _server.Given(Request.Create()
                .WithPath("/web/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var query = new WebSearchQuery("test", DateRange: DateRange.Week);
        await _client.SearchAsync(query);

        // Assert
        var requests = _server.LogEntries;
        requests.ShouldNotBeEmpty();
        var firstRequest = requests[0];
        firstRequest.RequestMessage?.Query?["freshness"].ToString().ShouldBe("pw");
    }

    [Fact]
    public async Task SearchAsync_ExtractsDomainCorrectly()
    {
        // Arrange
        var response = new
        {
            query = new { response_time = 0.1 },
            web = new
            {
                total_results = 3,
                results = new[]
                {
                    new { title = "Test 1", url = "https://www.example.com/page", description = "Desc 1" },
                    new { title = "Test 2", url = "https://subdomain.example.org/path", description = "Desc 2" },
                    new { title = "Test 3", url = "https://example.net", description = "Desc 3" }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/web/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var query = new WebSearchQuery("test");
        var result = await _client.SearchAsync(query);

        // Assert
        result.Results[0].Domain.ShouldBe("example.com"); // www. stripped
        result.Results[1].Domain.ShouldBe("subdomain.example.org");
        result.Results[2].Domain.ShouldBe("example.net");
    }

    [Fact]
    public async Task SearchAsync_TruncatesLongSnippets()
    {
        // Arrange
        var longDescription = new string('a', 300);
        var response = new
        {
            query = new { response_time = 0.1 },
            web = new
            {
                total_results = 1,
                results = new[]
                {
                    new { title = "Test", url = "https://example.com", description = longDescription }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/web/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var query = new WebSearchQuery("test");
        var result = await _client.SearchAsync(query);

        // Assert
        result.Results[0].Snippet.Length.ShouldBeLessThanOrEqualTo(203); // 200 + "..."
        result.Results[0].Snippet.ShouldEndWith("...");
    }

    [Fact]
    public async Task SearchAsync_OnHttpError_ThrowsException()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath("/web/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Unauthorized));

        // Act & Assert
        var query = new WebSearchQuery("test");
        await Should.ThrowAsync<HttpRequestException>(() => _client.SearchAsync(query));
    }

    public void Dispose()
    {
        _server.Dispose();
    }
}