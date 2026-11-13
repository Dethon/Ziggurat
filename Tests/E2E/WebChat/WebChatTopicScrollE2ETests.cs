using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The sidebar's second page, in a collection of its own: two real replies before the reload made it
// half of the topic management chain, which was the run's tail.
[Collection(WebChatE2ECollections.TopicScroll)]
[Trait("Category", "E2E")]
public class WebChatTopicScrollE2ETests(WebChatE2EFixture fixture)
{
    // The stack serves one row a page, so a second conversation is only reachable if the sidebar
    // asks for the page below its cursor. Real-browser scroll behaviour with rows reordering
    // underneath is the case a fake cannot reproduce.
    [SkippableFact]
    public async Task ScrollingTheSidebar_ReachesAConversationBelowTheFirstPage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        var tag = Guid.NewGuid().ToString("N")[..4];

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync($"Paged one {tag} for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-new:visible"));
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync($"Paged two {tag} for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // The rows reorder under every streamed chunk, and a scroll aimed at a moving list lands
        // wherever the list happens to be.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page, tag);

        // The reload is the point: it is the only way to see what one page of the sidebar
        // actually holds, because both conversations were created in this client and are held
        // whether they were paged to or not.
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        await page.EvaluateAsync(
            "() => { const r = document.querySelector('.hearth-rows'); if (r) r.scrollTop = r.scrollHeight; }");

        var older = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Paged one {tag}" });
        await older.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }
}