using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// The stale-ref walls as web_action speaks them: one test per wall, each sentence naming its own
// recovery, and no two refusals reading alike.
public class WebActionToolTests
{
    private readonly Mock<IWebBrowser> _browser = new();

    [Fact]
    public async Task ASupersededRef_SaysToSnapshotOrBrowseTheStillOpenPageAgain()
    {
        var envelope = await RunAsync(new WebActionResult(
            "s", WebActionStatus.RefSuperseded, null, false, null, null, null,
            RefUrl: "https://shop.test/product"));

        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.ElementNotFound);
        envelope["message"]!.GetValue<string>().ShouldContain("https://shop.test/product");
        var hint = envelope["hint"]!.GetValue<string>();
        hint.ShouldContain("web_snapshot");
        hint.ShouldContain("web_browse");
    }

    [Fact]
    public async Task ARefWhoseTabWasClosed_NamesTheUrlToBrowseAgain()
    {
        var envelope = await RunAsync(new WebActionResult(
            "s", WebActionStatus.RefClosed, null, false, null, null, null,
            RefUrl: "https://old.test/article"));

        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        envelope["message"]!.GetValue<string>().ShouldContain("https://old.test/article");
        envelope["message"]!.GetValue<string>().ShouldContain("closed");
        envelope["hint"]!.GetValue<string>().ShouldContain("Browse https://old.test/article again");
    }

    [Fact]
    public async Task EveryRefusal_ReadsDifferentlyFromEveryOther()
    {
        var refusals = new List<string>();
        foreach (var result in new[]
                 {
                     new WebActionResult("s", WebActionStatus.SessionNotFound, null, false, null, null,
                         "Session not found. Use web_browse first."),
                     new WebActionResult("s", WebActionStatus.ElementNotFound, null, false, null, null,
                         "Element e-3 is no longer on the page."),
                     new WebActionResult("s", WebActionStatus.Timeout, null, false, null, null,
                         "Operation timed out."),
                     new WebActionResult("s", WebActionStatus.RefSuperseded, null, false, null, null,
                         null, RefUrl: "https://a.test/"),
                     new WebActionResult("s", WebActionStatus.RefClosed, null, false, null, null,
                         null, RefUrl: "https://a.test/")
                 })
        {
            var envelope = await RunAsync(result);
            refusals.Add(envelope["message"]!.GetValue<string>());
        }

        refusals.Distinct().Count().ShouldBe(refusals.Count);
    }

    private async Task<JsonNode> RunAsync(WebActionResult result)
    {
        _browser
            .Setup(b => b.ActionAsync(It.IsAny<WebActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return await new TestableWebActionTool(_browser.Object).RunAsync();
    }

    private sealed class TestableWebActionTool(IWebBrowser browser) : WebActionTool(browser)
    {
        public async Task<JsonNode> RunAsync() =>
            ToJson(await ExecuteAsync("s", "e-1", WebActionType.Click, null, null, false, false,
                CancellationToken.None));
    }
}