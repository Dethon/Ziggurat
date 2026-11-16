using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.Tools;
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

    // Brave answers a missing or rejected key with a 422, its status for any bad request, and as a
    // bare 422 it reached the model as invalid_argument: it rewrote its query twice before giving up
    // on a search no query could fix. Both say so in the body, and are a credential's failure.
    [Theory]
    [InlineData("""{"error":{"code":"SUBSCRIPTION_TOKEN_INVALID","detail":"The provided subscription token is invalid.","meta":{"component":"authentication"},"status":422},"type":"ErrorResponse"}""")]
    [InlineData("""{"error":{"code":"VALIDATION","detail":"Unable to validate request parameter(s)","meta":{"errors":[{"input":null,"loc":["header","x-subscription-token"],"msg":"Field required","type":"missing"}]},"status":422},"type":"ErrorResponse"}""")]
    public async Task AKeyBraveRefuses_IsACredentialsFailure_NotTheQuerys(string body)
    {
        _server.Given(Request.Create().WithPath("/web/search").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

        var thrown = await Should.ThrowAsync<HttpRequestException>(() => _client.SearchAsync(new WebSearchQuery("q")));

        ToolError.CodeFor(thrown).ShouldBe(ToolError.Codes.Authentication);
    }

    [Fact]
    public async Task AQueryBraveCannotValidate_IsStillTheArgumentsFailure()
    {
        _server.Given(Request.Create().WithPath("/web/search").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"code":"VALIDATION","detail":"Unable to validate request parameter(s)","meta":{"errors":[{"loc":["query","count"],"msg":"too large","type":"less_than_equal"}]},"status":422},"type":"ErrorResponse"}"""));

        var thrown = await Should.ThrowAsync<HttpRequestException>(() => _client.SearchAsync(new WebSearchQuery("q")));

        ToolError.CodeFor(thrown).ShouldBe(ToolError.Codes.InvalidArgument);
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
    // The live Brave API does not return "total_results" at all -- a real response's web object
    // carries only type, results and family_friendly. TotalResults was a non-nullable long, so the
    // missing field deserialized to 0 and the "?? items.Count" fallback never fired (it only guards
    // a null web object, and web is present). Every search in production therefore reported
    // totalResults: 0 alongside a full results array. Every fixture here used to set the field, so
    // the tests only ever exercised the shape the API never sends.
    [Fact]
    public async Task SearchAsync_WhenResponseOmitsTotalResults_FallsBackToTheResultCount()
    {
        // Arrange - the shape the live API actually returns
        var response = new
        {
            query = new { response_time = 0.42 },
            web = new
            {
                type = "search",
                family_friendly = true,
                results = new[]
                {
                    new { title = "First", url = "https://example.com/1", description = "d1" },
                    new { title = "Second", url = "https://example.com/2", description = "d2" },
                    new { title = "Third", url = "https://example.com/3", description = "d3" }
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
        var result = await _client.SearchAsync(new WebSearchQuery("test query"));

        // Assert
        result.Results.Count.ShouldBe(3);
        result.TotalResults.ShouldBe(3);
    }

    // An explicit total_results still wins: it is the engine's own estimate of the whole corpus,
    // which is normally far larger than the page of results returned.
    [Fact]
    public async Task SearchAsync_WhenResponseCarriesTotalResults_KeepsTheReportedTotal()
    {
        // Arrange
        var response = new
        {
            query = new { response_time = 0.42 },
            web = new
            {
                total_results = 4820,
                results = new[]
                {
                    new { title = "First", url = "https://example.com/1", description = "d1" }
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
        var result = await _client.SearchAsync(new WebSearchQuery("test query"));

        // Assert
        result.Results.Count.ShouldBe(1);
        result.TotalResults.ShouldBe(4820);
    }
}