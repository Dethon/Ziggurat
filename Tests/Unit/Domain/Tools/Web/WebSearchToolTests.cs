using Domain.Contracts;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class WebSearchToolTests
{
    private readonly Mock<IWebSearchClient> _client = new();
    private readonly TestableWebSearchTool _tool;

    public WebSearchToolTests()
    {
        _tool = new TestableWebSearchTool(_client.Object);
    }

    private void StubResults(params WebSearchResultItem[] items)
    {
        _client.Setup(c => c.SearchAsync(It.IsAny<WebSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WebSearchQuery q, CancellationToken _) =>
                new WebSearchResult(q.Query, items.Length, items, "brave", 0.1));
    }

    [Fact]
    public async Task RunAsync_WithResults_MarksSnippetsAsStaleSummaries()
    {
        // A snippet is a summary written by somebody else at some other time; the page it points
        // at can contradict it. The result itself says so, at the moment the model reads it —
        // prose in the browsing prompt was tried and measurably changed nothing.
        StubResults(new WebSearchResultItem(
            "Museo del Carmen: horarios", "https://example.test/museo/horarios",
            "Abierto todos los días de 9:00 a 20:00", "example.test", null));

        var result = await _tool.TestRun("museo horarios", CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("success");
        var note = result["note"]!.GetValue<string>();
        note.ShouldContain("stale");
        note.ShouldContain("web_browse");
        result["results"]!.AsArray()[0]!["snippet"]!.GetValue<string>()
            .ShouldBe("Abierto todos los días de 9:00 a 20:00");
    }

    private class TestableWebSearchTool(IWebSearchClient client) : WebSearchTool(client)
    {
        public Task<System.Text.Json.Nodes.JsonNode> TestRun(string query, CancellationToken ct)
            => RunAsync(query, 10, null, null, ct);
    }
}