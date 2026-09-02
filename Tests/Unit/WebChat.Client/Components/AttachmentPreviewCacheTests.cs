using Domain.DTOs.WebChat;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

// A thumbnail's URL is a minted ticket with a lifetime, cached by the bubble that rendered it.
// The bubble outlives the ticket whenever the app sits in the background — so the cache, not
// the bubble, decides when a URL has to be minted again.
public sealed class AttachmentPreviewCacheTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(_start);
    private readonly AttachmentPreviewCache _sut;

    public AttachmentPreviewCacheTests()
    {
        _sut = new AttachmentPreviewCache(_clock);
    }

    [Fact]
    public void AnUnknownAttachmentIsStale()
    {
        _sut.Stale(["att-1"]).ShouldBe(["att-1"]);
        _sut.TryGetUrl("att-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void AHeldTicketWithTimeLeftIsServedFromTheCache()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));

        _sut.Stale(["att-1"]).ShouldBeEmpty();
        _sut.TryGetUrl("att-1", out var url).ShouldBeTrue();
        url.ShouldBe("https://x/dl/1");
    }

    // The browser fetches the image after the render, not during it, and a phone coming back
    // from the background may take seconds to do so. A ticket inside the margin is treated as
    // already gone rather than handed out to break on arrival.
    [Fact]
    public void ATicketInsideTheExpiryMarginIsStale()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));

        _clock.Advance(TimeSpan.FromMinutes(15) - AttachmentPreviewCache.Margin + TimeSpan.FromSeconds(1));

        _sut.Stale(["att-1"]).ShouldBe(["att-1"]);
        _sut.TryGetUrl("att-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void AnExpiredTicketIsStale()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));

        _clock.Advance(TimeSpan.FromHours(2));

        _sut.Stale(["att-1"]).ShouldBe(["att-1"]);
    }

    [Fact]
    public void HoldingAgainReplacesTheTicket()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));
        _clock.Advance(TimeSpan.FromHours(2));

        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/2", _clock.GetUtcNow().AddMinutes(15)));

        _sut.TryGetUrl("att-1", out var url).ShouldBeTrue();
        url.ShouldBe("https://x/dl/2");
    }

    // The server can forget a ticket before its time — a restart while the app was in the
    // background prunes every one it held. The image failing to load is the only signal, and
    // forgetting the URL is what lets the next render mint another.
    [Fact]
    public void TheFirstFailureMakesTheAttachmentStaleAgain()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));

        _sut.Failed("att-1").ShouldBeTrue();

        _sut.Stale(["att-1"]).ShouldBe(["att-1"]);
        _sut.TryGetUrl("att-1", out _).ShouldBeFalse();
    }

    // A fresh ticket that also fails is a file that is gone, not a ticket that was. Minting
    // again would loop for as long as the bubble is on screen, so the second failure gives up:
    // the attachment is neither served nor asked about, and the bubble falls back to a chip.
    [Fact]
    public void ASecondFailureGivesUpOnTheAttachment()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));
        _sut.Failed("att-1");
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/2", _start.AddMinutes(15)));

        _sut.Failed("att-1").ShouldBeFalse();

        _sut.Stale(["att-1"]).ShouldBeEmpty();
        _sut.TryGetUrl("att-1", out _).ShouldBeFalse();
    }

    // The replacement ticket loading is proof the file is there, so the failure it recovered
    // from stops counting: a second prune, days later in the same bubble, is retried like the first.
    [Fact]
    public void ALoadedReplacementForgivesTheEarlierFailure()
    {
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/1", _start.AddMinutes(15)));
        _sut.Failed("att-1");
        _sut.Hold("att-1", new AttachmentDownload("https://x/dl/2", _start.AddMinutes(15)));
        _sut.Loaded("att-1");

        _sut.Failed("att-1").ShouldBeTrue();
    }

    [Fact]
    public void OnlyTheStaleOnesAreReported()
    {
        _sut.Hold("fresh", new AttachmentDownload("https://x/dl/f", _start.AddMinutes(15)));
        _sut.Hold("old", new AttachmentDownload("https://x/dl/o", _start.AddSeconds(1)));
        _clock.Advance(TimeSpan.FromMinutes(1));

        _sut.Stale(["fresh", "old", "never"]).ShouldBe(["old", "never"]);
    }
}