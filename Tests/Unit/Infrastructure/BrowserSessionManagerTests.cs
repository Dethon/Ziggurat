using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class BrowserSessionManagerTests
{
    private static (Mock<IBrowserContext> Context, Mock<IPage> Page) CreateMocks()
    {
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()))
            .Returns(Task.CompletedTask);

        var ctx = new Mock<IBrowserContext>();
        ctx.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);
        return (ctx, page);
    }

    [Fact]
    public async Task PruneIdleAsync_AfterAccess_ResetsIdleClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(25));

        // Re-access just before threshold
        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        // Total elapsed since last access = 10 min < 30 min threshold
        manager.Get("s1").ShouldNotBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task PruneIdleAsync_OnlyRemovesIdleSessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx1, page1) = CreateMocks();
        var (ctx2, page2) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("idle", ctx1.Object);
        time.Advance(TimeSpan.FromMinutes(25));
        await manager.GetOrCreateAsync("active", ctx2.Object);
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        manager.Get("idle").ShouldBeNull();
        manager.Get("active").ShouldNotBeNull();
        page1.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        page2.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task Clear_DropsAllSessions()
    {
        var (ctx, _) = CreateMocks();
        await using var manager = new BrowserSessionManager();

        await manager.GetOrCreateAsync("s1", ctx.Object);
        await manager.GetOrCreateAsync("s2", ctx.Object);

        manager.Clear();

        manager.Get("s1").ShouldBeNull();
        manager.Get("s2").ShouldBeNull();
    }

    [Fact]
    public async Task AcquireSessionLockAsync_SameSession_BlocksUntilReleased()
    {
        await using var manager = new BrowserSessionManager();

        var first = await manager.AcquireSessionLockAsync("s1");
        var second = manager.AcquireSessionLockAsync("s1");

        // A second acquisition for the same session must wait for the first to release.
        (await Task.WhenAny(second, Task.Delay(200))).ShouldNotBe(second);

        first.Dispose();

        // Once released, the queued acquisition completes.
        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);
        (await second).Dispose();
    }

    [Fact]
    public async Task AcquireSessionLockAsync_DifferentSessions_DoNotBlockEachOther()
    {
        await using var manager = new BrowserSessionManager();

        using var first = await manager.AcquireSessionLockAsync("s1");
        var second = manager.AcquireSessionLockAsync("s2");

        // A different session must acquire immediately, even while another lock is held.
        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);
        (await second).Dispose();
    }

    [Fact]
    public async Task BackgroundTimer_FiresPrune_OnPruneInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30),
            pruneInterval: TimeSpan.FromMinutes(5));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(31));

        // The prune runs from a timer callback, so a hundred milliseconds was a bet that the
        // callback got a thread inside them rather than a statement about pruning.
        await Eventually.Until(() => manager.Get("s1") is null, "the idle session to be pruned");
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }
}