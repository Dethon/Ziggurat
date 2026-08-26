using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class WebBrowseToolTests
{
    private readonly Mock<IWebBrowser> _browser = new();

    [Fact]
    public async Task ASnapshotRequestedWithTheBrowse_AsksForThePageTheBrowseLandedOn()
    {
        // Parallel browses of one session land on different tabs; a follow-up snapshot that just
        // takes "the current tab" can attach another browse's refs to this browse's text — the
        // silent wrong-target answer routed refs exist to prevent. The snapshot names the page.
        SetUpNavigate(Result("https://landed.test/final"));
        SnapshotRequest? seen = null;
        _browser
            .Setup(b => b.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SnapshotRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(new SnapshotResult("s", "https://landed.test/final", "- link", 1, null));

        await new TestableWebBrowseTool(_browser.Object).RunAsync(snapshot: true);

        seen.ShouldNotBeNull();
        seen.ForUrl.ShouldBe("https://landed.test/final");
    }

    [Fact]
    public async Task ATruncatedPage_NamesTheOffsetTheNextWindowStartsAt()
    {
        // Truncation backs the cut up past a partial image entry or to a newline, so the window
        // ends short of offset + maxLength. Paging by maxLength then skips what the back-up left —
        // including entries the envelope just promised lay ahead. The envelope names the exact
        // continuation the processor measured.
        SetUpNavigate(Result("https://a.test/", Content: new string('x', 700),
            ContentLength: 5000, Truncated: true) with
        { NextOffset = 1680 });

        var result = await new TestableWebBrowseTool(_browser.Object).RunAsync(offset: 1000);

        result.Envelope["nextOffset"]!.GetValue<int>().ShouldBe(1680);
    }

    [Fact]
    public async Task APageThatFitsWhole_NamesNoNextOffset()
    {
        SetUpNavigate(Result("https://a.test/", Content: "all of it",
            ContentLength: 9, Truncated: false));

        var result = await new TestableWebBrowseTool(_browser.Object).RunAsync();

        result.Envelope["nextOffset"].ShouldBeNull();
    }

    private void SetUpNavigate(BrowseResult result) =>
        _browser
            .Setup(b => b.NavigateAsync(It.IsAny<BrowseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static BrowseResult Result(
        string url, string Content = "body", int ContentLength = 4, bool Truncated = false) =>
        new("s", url, BrowseStatus.Success, "Title", Content, ContentLength, Truncated,
            null, null, null, null);

    private sealed class TestableWebBrowseTool(IWebBrowser browser) : WebBrowseTool(browser)
    {
        public Task<WebBrowseToolResult> RunAsync(
            int offset = 0, bool snapshot = false) =>
            RunAsync("s", "https://a.test/", null, 10000, offset,
                useReadability: false, scrollToLoad: false, scrollSteps: 3, snapshot,
                CancellationToken.None);
    }
}