using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using Xunit.Abstractions;

using static Tests.E2E.WebChat.HearthFlickE2ETests;

namespace Tests.E2E.WebChat;

// The open sheet's list still scrolls under a finger. Out of HearthFlickE2ETests by length, into a
// collection of its own: each case there makes topics on the real model and waits for their rows to
// settle, and as one chain of three they were among the run's longest on a slow day.
[Collection(WebChatE2ECollections.ListScroll)]
[Trait("Category", "E2E")]
public sealed class HearthListScrollE2ETests(WebChatE2EFixture fixture)
{
    // The cure for the flick is a non-passive `touchmove` listener on the whole sheet, so the one
    // thing it could plausibly break is the conversation list's own scrolling. `_onDown` bails for
    // targets inside `.hearth-rows` without setting `_dragging`, which is the gate that keeps the
    // list native; this proves that gate end to end rather than by reading it.
    [SkippableFact]
    public async Task MobileViewport_WithTheSheetOpen_DraggingTheConversationListStillScrollsIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex(), hasTouch: true, isMobile: true);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"Scroll one {tag} answer briefly");
        await CreateTopicAsync(page, $"Scroll two {tag} answer briefly");
        await CreateTopicAsync(page, $"Scroll three {tag} answer briefly");
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page, tag);

        await EnsurePeekAsync(page);
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var handle = await CentreOfAsync(page, ".hearth-handle");
        await DragAsync(cdp, handle.X, handle.Y, handle.Y - 560, 56, 18, frameMs: 34);
        await WaitForSheetSettledAsync(page);

        // A handful of seeded rows need not overflow a full-height sheet, and a list that cannot
        // scroll would pass this test for the wrong reason. Shrink the scroller so the rows it
        // already holds overflow it.
        await page.EvaluateAsync("() => document.querySelector('.hearth-rows').style.maxHeight = '120px'");
        var overflows = await page.EvaluateAsync<bool>(
            "() => { const r = document.querySelector('.hearth-rows'); return r.scrollHeight > r.clientHeight + 8; }");
        overflows.ShouldBeTrue("the conversation list does not overflow, so this test would prove nothing");

        var (x, y) = await CentreOfAsync(page, ".hearth-rows");
        await DragAsync(cdp, x, y + 40, y - 40, 16, 12, frameMs: 28);

        // The scroll, not half a second of hoping for it. A flick keeps moving after the finger
        // leaves, so how long it takes to leave zero belongs to the machine: half a second is
        // plenty on a quiet one and not always enough on a loaded one, where this failed as the
        // handler swallowing a scroll that was merely late. Waiting for the state itself both
        // returns the moment it arrives and says the same thing on any machine.
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.hearth-rows').scrollTop > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var scrollTop = await page.EvaluateAsync<double>(
            "() => document.querySelector('.hearth-rows').scrollTop");
        scrollTop.ShouldBeGreaterThan(0, "the sheet's touchmove handler swallowed the list's own scrolling");
    }
}