using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A ref the model can read is always a ref it can use, so a body cut short never ends mid-entry.
public class TruncationImageEntryTests
{
    [Fact]
    public void AnEntryStraddlingTheCut_IsDroppedWhole()
    {
        // The entry shares its line with the prose before it -- the case the newline rule cannot
        // rescue, because backing up to the last line boundary lands well before the cut and the
        // 70% guard declines it.
        var text = "\n" + new string('a', 200) + " " + PageImageEntry.Write(PageImageEntry.RefFor(1), "A harbour at dusk");

        // Lands inside the entry: past its opening bracket, short of its closing one.
        var truncated = HtmlConverter.Truncate(text, 225);

        truncated.ShouldNotContain("[image");
        truncated.ShouldContain("[Content truncated...]");
    }

    [Fact]
    public void AnEntryEndingExactlyAtTheCut_IsKeptIntact()
    {
        var entry = PageImageEntry.Write(PageImageEntry.RefFor(1), "A harbour at dusk");
        var text = "\n" + new string('a', 200) + " " + entry;

        // maxLength reserves 20 for the suffix, so the cut lands exactly on the entry's close.
        var truncated = HtmlConverter.Truncate(text + new string('b', 500), text.Length + 20);

        truncated.ShouldContain(entry);
    }

    [Fact]
    public void ABodyCutMidEntry_KeepsEveryEarlierEntryWhole()
    {
        var first = PageImageEntry.Write(PageImageEntry.RefFor(1), "First picture");
        var second = PageImageEntry.Write(PageImageEntry.RefFor(2), "Second picture");
        var text = "\n" + first + " " + new string('a', 200) + " " + second;

        var truncated = HtmlConverter.Truncate(text, 250);

        truncated.ShouldContain(first);
        truncated.ShouldNotContain("[image i-2");
    }

    [Fact]
    public void ACutLandingOnOrdinaryText_StillBacksUpToTheLineBoundaryAsBefore()
    {
        var text = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of the page"));

        var truncated = HtmlConverter.Truncate(text, 500);

        truncated.ShouldContain("[Content truncated...]");
        truncated.ShouldNotContain("[image");
    }

    [Fact]
    public void ProseCarryingTheWordsOfAnEntry_IsNotTrimmedForThem()
    {
        // "[image " in ordinary page text is page text. Only the entry's real shape counts.
        var text = "\n" + new string('a', 180) + " see the [image gallery for more of these";

        var truncated = HtmlConverter.Truncate(text, 225);

        truncated.ShouldContain("[image gallery");
    }

    [Fact]
    public async Task ImagesBeyondTheReturnedWindow_AreCounted()
    {
        var html = Page(string.Join("\n", Enumerable.Range(1, 12).Select(i =>
            $"""<p>{new string('x', 200)}</p><img src="/p{i}.jpg" alt="Picture {i}" data-img-w="300" data-img-h="300">""")));
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", MaxLength: 900);

        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        result.ImageCount.ShouldBeGreaterThan(0);
        result.ImageCount.ShouldBeLessThan(12);
        result.ImagesBeyondWindow.ShouldBe(12 - result.ImageCount);
    }

    [Fact]
    public async Task ImagesBehindTheWindow_AreNotReportedAsBeyondIt()
    {
        // "Beyond" promises that paging forward reaches them. An offset mid-page has entries on
        // both sides; counting the ones already paged past would send the model forward chasing
        // pictures that are behind it.
        var html = Page(string.Join("\n", Enumerable.Range(1, 12).Select(i =>
            $"""<p>{new string('x', 200)}</p><img src="/p{i}.jpg" alt="Picture {i}" data-img-w="300" data-img-h="300">""")));

        var whole = await HtmlProcessor.ProcessAsync(
            new BrowseRequest("test", "http://example.com/test", MaxLength: 100000), html, CancellationToken.None);
        var offset = whole.Content!.IndexOf("[image i-4", StringComparison.Ordinal);

        var window = await HtmlProcessor.ProcessAsync(
            new BrowseRequest("test", "http://example.com/test", MaxLength: 100000, Offset: offset),
            html, CancellationToken.None);

        window.ImageCount.ShouldBe(9);
        window.ImagesBeyondWindow.ShouldBe(0);
    }

    [Fact]
    public async Task APageWhoseImagesAllFit_ReportsNoneBeyondTheWindow()
    {
        var html = Page("""<img src="/p.jpg" alt="Only picture" data-img-w="300" data-img-h="300">""");
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", MaxLength: 100000);

        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        result.ImageCount.ShouldBe(1);
        result.ImagesBeyondWindow.ShouldBe(0);
    }

    [Fact]
    public async Task TruncatingAnImageHeavyPage_LeavesThePagingArithmeticUnchanged()
    {
        // contentLength stays the pre-slice total, so an offset the model computes from it still
        // lands where it expects even though entries shifted the text window.
        var html = Page(string.Join("\n", Enumerable.Range(1, 10).Select(i =>
            $"""<p>{new string('x', 200)}</p><img src="/p{i}.jpg" alt="Picture {i}" data-img-w="300" data-img-h="300">""")));

        var whole = await HtmlProcessor.ProcessAsync(
            new BrowseRequest("test", "http://example.com/test", MaxLength: 100000), html, CancellationToken.None);
        var window = await HtmlProcessor.ProcessAsync(
            new BrowseRequest("test", "http://example.com/test", MaxLength: 700), html, CancellationToken.None);

        window.ContentLength.ShouldBe(whole.ContentLength);
    }

    private static string Page(string body) =>
        $"""
         <!DOCTYPE html>
         <html>
         <head><title>Test</title></head>
         <body>{body}</body>
         </html>
         """;
}