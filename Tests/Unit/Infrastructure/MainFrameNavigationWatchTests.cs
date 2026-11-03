using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// Whether the page has sent itself somewhere it has not arrived yet. Firefox reports a navigation's
// request from its parent process and a commit from the page's own, so under load the commit of the
// document already on screen can be reported after the request its script made next: a busy suite
// read the bot check itself ("One moment.") because that late commit cleared the follow-up still in
// flight.
public sealed class MainFrameNavigationWatchTests
{
    private readonly Mock<IPage> _page = new();
    private readonly Mock<IFrame> _mainFrame = new();

    private IRequest Navigation(string url)
    {
        var request = new Mock<IRequest>();
        request.SetupGet(r => r.IsNavigationRequest).Returns(true);
        request.SetupGet(r => r.Frame).Returns(_mainFrame.Object);
        request.SetupGet(r => r.Url).Returns(url);
        return request.Object;
    }

    private void Requested(string url) => _page.Raise(p => p.Request += null, _page.Object, Navigation(url));

    private void Committed(string url)
    {
        _mainFrame.SetupGet(f => f.Url).Returns(url);
        _page.Raise(p => p.FrameNavigated += null, _page.Object, _mainFrame.Object);
    }

    [Fact]
    public void TheEarlierDocumentsCommit_ReportedAfterTheNextRequest_LeavesTheNextOneInFlight()
    {
        using var watch = new MainFrameNavigationWatch(_page.Object);

        Requested("https://site.test/thread?solution=1");
        Committed("https://site.test/thread");

        watch.InFlight.ShouldBeTrue();
    }

    [Fact]
    public void TheRequestedDocumentsCommit_EndsTheFlight()
    {
        using var watch = new MainFrameNavigationWatch(_page.Object);

        Requested("https://site.test/thread?solution=1");
        Committed("https://site.test/thread?solution=1");

        watch.InFlight.ShouldBeFalse();
    }

    // A redirect is a request of its own for every hop, and the page commits the last.
    [Fact]
    public void ARedirectedNavigation_EndsAtTheLastHopsCommit()
    {
        using var watch = new MainFrameNavigationWatch(_page.Object);

        Requested("https://site.test/login");
        Requested("https://site.test/home");
        Committed("https://site.test/home#top");

        watch.InFlight.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitAsync_AnswersOnceTheRequestedDocumentCommits()
    {
        using var watch = new MainFrameNavigationWatch(_page.Object);
        Requested("https://site.test/thread?solution=1");

        var waiting = watch.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Committed("https://site.test/thread");
        waiting.IsCompleted.ShouldBeFalse();
        Committed("https://site.test/thread?solution=1");

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }
}