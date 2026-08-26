using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// Six refusals, each naming its own wall, because a model told only "unavailable" either retries a
// permanent failure or abandons a retryable one.
public class ViewImageToolTests
{
    private readonly Mock<IWebBrowser> _browser = new();

    [Fact]
    public async Task ACallNamingRefs_ReturnsThosePictures()
    {
        Answers(("i-1", ImageFetchStatus.Success), ("i-2", ImageFetchStatus.Success));

        var result = await RunAsync(["i-1", "i-2"]);

        result.Images.Count.ShouldBe(2);
        result.Images.ShouldAllBe(i => i.Bytes != null);
    }

    [Fact]
    public async Task ACallOverTheCap_FetchesTheFirstEightAndNamesTheRest()
    {
        var refs = Enumerable.Range(1, 11).Select(i => $"i-{i}").ToList();
        Answers(refs.Take(8).Select(r => (r, ImageFetchStatus.Success)).ToArray());

        var result = await RunAsync(refs);

        result.Images.Count.ShouldBe(8);
        var deferred = result.Envelope["deferredRefs"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        deferred.ShouldBe(["i-9", "i-10", "i-11"]);
        // Partial success is success: a greedy call progresses and learns the rule.
        result.Envelope["status"]!.GetValue<string>().ShouldBe("success");
    }

    [Fact]
    public async Task ExactlyEightRefs_AreAllFetchedWithNothingDeferred()
    {
        var refs = Enumerable.Range(1, 8).Select(i => $"i-{i}").ToList();
        Answers(refs.Select(r => (r, ImageFetchStatus.Success)).ToArray());

        var result = await RunAsync(refs);

        result.Images.Count.ShouldBe(8);
        result.Envelope["deferredRefs"].ShouldBeNull();
    }

    [Fact]
    public async Task AForeignRefBeyondTheCap_IsDeferredRatherThanRefusingTheCall()
    {
        // The cap cuts before shape is examined: a greedy call with a stray e- ref past the cut
        // still progresses on the eight it asked for first, and the stray gets its refusal when
        // it is actually asked for.
        var refs = Enumerable.Range(1, 8).Select(i => $"i-{i}").Append("e-3").ToList();
        Answers(refs.Take(8).Select(r => (r, ImageFetchStatus.Success)).ToArray());

        var result = await RunAsync(refs);

        result.Images.Count.ShouldBe(8);
        result.Envelope["status"]!.GetValue<string>().ShouldBe("success");
        result.Envelope["deferredRefs"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe(["e-3"]);
    }

    [Fact]
    public async Task ARefFromAClosedSession_SaysToBrowseThePageAgain()
    {
        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s", [], SessionMissing: true));

        var result = await RunAsync(["i-1"]);

        result.Images.ShouldBeEmpty();
        Message(result).ShouldContain("browse");
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.SessionNotFound);
    }

    [Fact]
    public async Task AnElementRef_IsRefusedByNameWithoutReachingTheBrowser()
    {
        // A ref's shape is what says which tool it was meant for, so web_action's refs are turned
        // away here rather than failing to be found on the page.
        var result = await RunAsync(["e-3"]);

        result.Images.ShouldBeEmpty();
        Message(result).ShouldContain("e-3");
        Message(result).ShouldContain("web_action");
        _browser.Verify(
            b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ARefThatNamesNoImageOnThePage_GetsItsOwnMessage()
    {
        Answers(("i-9", ImageFetchStatus.NotAnImageRef));

        var result = await RunAsync(["i-9"]);

        var note = result.Notes.ShouldHaveSingleItem();
        note.ShouldContain("i-9");
        note.ShouldNotContain("browse the page again");
    }

    [Fact]
    public async Task ASiteRefusingTheFetch_GetsItsOwnMessage()
    {
        Answers(("i-1", ImageFetchStatus.SiteRefused));

        var result = await RunAsync(["i-1"]);

        result.Notes.ShouldHaveSingleItem().ShouldContain("refused");
    }

    [Fact]
    public async Task AModelThatCannotAcceptImages_IsToldSoAndNothingIsFetched()
    {
        var result = await RunAsync(["i-1"], acceptsImages: false);

        result.Images.ShouldBeEmpty();
        Message(result).ShouldContain("does not accept images");
        _browser.Verify(
            b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnEmptyListOfRefs_IsRefusedRatherThanFetchingNothing()
    {
        var result = await RunAsync([]);

        result.Images.ShouldBeEmpty();
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task ASupersededRef_SaysToSnapshotOrBrowseTheStillOpenPageAgain()
    {
        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s",
                [new FetchedImage("i-3", null, null, ImageFetchStatus.RefSuperseded,
                    Url: "https://shop.test/product")]));

        var result = await RunAsync(["i-3"]);

        var note = result.Notes.ShouldHaveSingleItem();
        note.ShouldContain("i-3");
        note.ShouldContain("https://shop.test/product");
        note.ShouldContain("browse", Case.Insensitive);
    }

    [Fact]
    public async Task ARefWhoseTabWasClosed_NamesTheUrlItBelongedTo()
    {
        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s",
                [new FetchedImage("i-7", null, null, ImageFetchStatus.RefClosed,
                    Url: "https://old.test/article")]));

        var result = await RunAsync(["i-7"]);

        var note = result.Notes.ShouldHaveSingleItem();
        note.ShouldContain("i-7");
        note.ShouldContain("https://old.test/article");
        note.ShouldContain("closed");
    }

    [Fact]
    public async Task EveryRefusal_ReadsDifferentlyFromEveryOther()
    {
        // The whole point of eight sentences rather than one: a model can tell a retryable failure
        // from a permanent one, and a page it can refresh from a page that is gone.
        Answers(
            ("i-1", ImageFetchStatus.NotAnImageRef),
            ("i-2", ImageFetchStatus.SiteRefused));

        var mixed = await RunAsync(["i-1", "i-2"]);
        var noVision = Message(await RunAsync(["i-1"], acceptsImages: false));
        var wrongNamespace = Message(await RunAsync(["e-1"]));

        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s",
                [new FetchedImage("i-1", null, null, ImageFetchStatus.RefSuperseded, Url: "https://a.test/"),
                 new FetchedImage("i-2", null, null, ImageFetchStatus.RefClosed, Url: "https://a.test/")]));
        var walls = (await RunAsync(["i-1", "i-2"])).Notes;

        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s", [], SessionMissing: true));
        var deadSession = Message(await RunAsync(["i-1"]));

        var sentences = mixed.Notes.Concat(walls).Concat([noVision, wrongNamespace, deadSession]).ToList();
        sentences.Distinct().Count().ShouldBe(sentences.Count);
    }

    private static string Message(ViewImageToolResult result) =>
        result.Envelope["message"]?.GetValue<string>()
        ?? string.Join(" ", result.Notes);

    private void Answers(params (string Ref, ImageFetchStatus Status)[] answers) =>
        _browser
            .Setup(b => b.FetchImagesAsync(It.IsAny<ImageFetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageFetchResult("s", answers
                .Select(a => new FetchedImage(
                    a.Ref,
                    a.Status == ImageFetchStatus.Success ? "image/jpeg" : null,
                    a.Status == ImageFetchStatus.Success ? [1, 2, 3] : null,
                    a.Status))
                .ToList()));

    private Task<ViewImageToolResult> RunAsync(IReadOnlyList<string> refs, bool acceptsImages = true) =>
        new TestableViewImageTool(_browser.Object).Run("session", refs, acceptsImages, CancellationToken.None);

    private sealed class TestableViewImageTool(IWebBrowser browser) : ViewImageTool(browser)
    {
        public Task<ViewImageToolResult> Run(
            string sessionId, IReadOnlyList<string> refs, bool acceptsImages, CancellationToken ct) =>
            RunAsync(sessionId, refs, acceptsImages, ct);
    }
}