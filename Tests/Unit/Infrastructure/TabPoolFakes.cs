using Microsoft.Playwright;
using Moq;

namespace Tests.Unit.Infrastructure;

// A fake Playwright page for driving the tab pool through its public seam: the closed flag and
// the address can be flipped mid-protocol, which is how a test stages an eviction race or an
// in-work navigation without a browser.
internal sealed class FakePage
{
    public required Mock<IPage> Mock { get; init; }
    public bool Closed { get; set; }
    public string Url { get; set; } = "about:blank";

    public static FakePage Create(string url = "about:blank", bool closed = false)
    {
        var fake = new FakePage { Mock = new Mock<IPage>(), Url = url, Closed = closed };
        fake.Mock.SetupGet(p => p.IsClosed).Returns(() => fake.Closed);
        fake.Mock.SetupGet(p => p.Url).Returns(() => fake.Url);
        fake.Mock.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()))
            .Returns(Task.CompletedTask);
        return fake;
    }

    // Fires the page's Popup event the way a site's window.open does, which is how a popup enters
    // the pool: through the handler the pool itself registered at admission. Both delegate
    // arguments are passed because the payload is not an EventArgs.
    public void RaisePopup(FakePage popup) =>
        Mock.Raise(p => p.Popup += null, Mock.Object, popup.Mock.Object);
}

internal static class TabPoolFakes
{
    // A context that hands out a fresh page per NewPageAsync call, recorded in order, so a test
    // can say which tab's page was closed and which stayed alive.
    public static (Mock<IBrowserContext> Context, List<FakePage> Pages) CreateContext(
        bool pagesStartClosed = false)
    {
        var pages = new List<FakePage>();
        var ctx = new Mock<IBrowserContext>();
        ctx.Setup(c => c.NewPageAsync()).ReturnsAsync(() =>
        {
            var fake = FakePage.Create(closed: pagesStartClosed);
            pages.Add(fake);
            return fake.Mock.Object;
        });
        return (ctx, pages);
    }
}