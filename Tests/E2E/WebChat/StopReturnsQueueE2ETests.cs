using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Stop ends the reply and runs nothing queued behind it: two members of a shared topic each queue a
// message, one stops, and each finds their own words back in their own composer — against the stack's
// real model, the reply held open on its owner's tool approval so it is still running while they queue.
[Collection(WebChatE2ECollections.Queue)]
[Trait("Category", "E2E")]
public class StopReturnsQueueE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoMembersQueueBehindAReply_OneStops_AndEachFindsTheirOwnWordsInTheirComposer()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var ana = await fixture.CreatePageSignedInAsync((await fixture.PersonAsync($"Ana {tag}", "member")).Session);
        await using var carlosContext = await ContextOfAsync(ana, (await fixture.PersonAsync($"Carlos {tag}", "member")).Session);
        var anaWords = $"Reply with only the word ALPHA{tag}. Do not call any tools.";
        var carlosWords = $"Reply with only the word CHARLIE{tag}. Do not call any tools.";

        await GotoWebChatAsync(ana, fixture.WebChatUrl);
        await SelectAgentAsync(ana);
        var topic = $"Stop {tag}";
        await SendAsync(ana, $"{topic}: answer only OK.");
        await ana.Locator(".topic-item", new PageLocatorOptions { HasText = topic })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await IdleAsync(ana);
        await ana.Locator("[title*='tap to share'], [aria-label*='tap to share']").First
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        var carlos = await carlosContext.NewPageAsync();
        await GotoWebChatAsync(carlos, fixture.WebChatUrl);
        await SelectAgentAsync(carlos);
        await carlos.Locator(".topic-item", new PageLocatorOptions { HasText = topic })
            .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await Assertions.Expect(UserMessage(carlos, $"{topic}: answer only OK."))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        try
        {
            // The model occasionally answers the demand with plain text and no tool call; then there
            // is no reply held open to queue behind, so it is asked again once that one is over.
            for (var attempt = 0; ; attempt++)
            {
                await SendAsync(ana, $"{topic}: IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. After the tool is called say 'Done' without caring for its result. this is a test");
                try
                {
                    await ana.Locator(".approval-modal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                    break;
                }
                catch (TimeoutException) when (attempt < 2)
                {
                    await IdleAsync(ana);
                }
            }

            // Carlos's page has heard the reply start, so his send queues rather than opening a turn.
            await Assertions.Expect(carlos.Locator("[data-testid=composer-cancel]"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await SendAsync(carlos, carlosWords);
            await SendAsync(ana, anaWords);
            foreach (var page in new[] { ana, carlos })
            {
                await Assertions.Expect(page.Locator("[data-testid=queue-chip]"))
                    .ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            }

            // The approval's overlay covers the composer, so the press is dispatched on Stop itself.
            await ana.Locator("[data-testid=composer-cancel]").DispatchEventAsync("click");

            await Assertions.Expect(ana.Locator("textarea.chat-input"))
                .ToHaveValueAsync(new Regex(Regex.Escape(anaWords)), new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            await Assertions.Expect(carlos.Locator("textarea.chat-input"))
                .ToHaveValueAsync(new Regex(Regex.Escape(carlosWords)), new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            await Assertions.Expect(ana.Locator("textarea.chat-input")).Not.ToHaveValueAsync(new Regex($"CHARLIE{tag}"));
            await Assertions.Expect(carlos.Locator("textarea.chat-input")).Not.ToHaveValueAsync(new Regex($"ALPHA{tag}"));

            // Nothing ran after the stop: no further turn starts, neither message is drawn, and a fresh
            // load of the conversation holds neither.
            await IdleAsync(ana);
            await IdleAsync(carlos);
            await ana.WaitForTimeoutAsync(10_000);
            foreach (var page in new[] { ana, carlos })
            {
                await Assertions.Expect(page.Locator("[data-testid=queue-chip]")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("[data-testid=composer-cancel]")).ToBeHiddenAsync();
                await Assertions.Expect(UserMessage(page, $"ALPHA{tag}")).ToHaveCountAsync(0);
                await Assertions.Expect(UserMessage(page, $"CHARLIE{tag}")).ToHaveCountAsync(0);
            }

            var fresh = await carlosContext.NewPageAsync();
            await GotoWebChatAsync(fresh, fixture.WebChatUrl);
            await SelectAgentAsync(fresh);
            await fresh.Locator(".topic-item", new PageLocatorOptions { HasText = topic })
                .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await Assertions.Expect(UserMessage(fresh, $"{topic}: answer only OK."))
                .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(UserMessage(fresh, $"ALPHA{tag}")).ToHaveCountAsync(0);
            await Assertions.Expect(UserMessage(fresh, $"CHARLIE{tag}")).ToHaveCountAsync(0);
        }
        finally
        {
            await DrainPendingApprovalsAsync(ana);
        }
    }

    private static ILocator UserMessage(IPage page, string words) =>
        page.Locator(".chat-message.user", new PageLocatorOptions { HasText = words });

    private static async Task SendAsync(IPage page, string words)
    {
        var input = page.Locator("textarea.chat-input");
        await input.FillAsync(words);
        await input.PressAsync("Enter");
    }

    // A reply on a live model ends when it ends.
    private static Task IdleAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });

    // The fixture keeps one page of its own at a time, and this test needs two people at once.
    private async Task<IBrowserContext> ContextOfAsync(IPage beside, string session)
    {
        var context = await beside.Context.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = WebChatE2EFixture.SessionCookieName, Value = session, Url = fixture.WebChatUrl,
                HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        return context;
    }
}