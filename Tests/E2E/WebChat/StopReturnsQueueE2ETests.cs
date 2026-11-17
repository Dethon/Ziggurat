using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.QueueE2E;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Stop ends the reply and runs nothing queued behind it: two members of a shared topic each queue a
// message, one stops, and each finds their own words back in their own composer — the reply held open on
// its owner's tool approval so it is still running while they queue.
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
        await using var carlosContext = await ContextOfAsync(
            ana, (await fixture.PersonAsync($"Carlos {tag}", "member")).Session, fixture.WebChatUrl);
        var anaWords = $"Reply with only the word ALPHA{tag}. Do not call any tools.";
        var carlosWords = $"Reply with only the word CHARLIE{tag}. Do not call any tools.";

        await GotoWebChatAsync(ana, fixture.WebChatUrl);
        await SelectAgentAsync(ana);
        var topic = $"Stop {tag}";
        var carlos = await carlosContext.NewPageAsync();

        try
        {
            await HoldOnAnApprovalAsync(ana, $"{topic}: {ToolDemand}");

            // Shared while its reply waits on the approval, whose overlay covers the header: the press is
            // dispatched on the toggle itself.
            var share = ana.Locator("[title*='tap to share']").First;
            await share.DispatchEventAsync("click");
            await Assertions.Expect(ana.Locator("[title*='tap to make it yours alone']"))
                .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

            await GotoWebChatAsync(carlos, fixture.WebChatUrl);
            await SelectAgentAsync(carlos);
            await OpenTopicPastTheOverlayAsync(carlos, topic);

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

            // Sent, so the composers are empty: whatever is in them after the stop came back from it.
            await Assertions.Expect(ana.Locator("textarea.chat-input")).ToHaveValueAsync("");
            await Assertions.Expect(carlos.Locator("textarea.chat-input")).ToHaveValueAsync("");

            // The approval's overlay covers the composer, so the press is dispatched on Stop itself.
            await ana.Locator("[data-testid=composer-cancel]").DispatchEventAsync("click");

            await Assertions.Expect(ana.Locator("textarea.chat-input"))
                .ToHaveValueAsync(new Regex(Regex.Escape(anaWords)), new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            await Assertions.Expect(carlos.Locator("textarea.chat-input"))
                .ToHaveValueAsync(new Regex(Regex.Escape(carlosWords)), new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            await Assertions.Expect(ana.Locator("textarea.chat-input")).Not.ToHaveValueAsync(new Regex($"CHARLIE{tag}"));
            await Assertions.Expect(carlos.Locator("textarea.chat-input")).Not.ToHaveValueAsync(new Regex($"ALPHA{tag}"));

            // Nothing ran after the stop: no further turn starts, neither message is drawn, and a fresh
            // load of the conversation holds neither. A queued turn starting would be written on the
            // stream, so drawn and Stop shown on both pages, the moment it was dispatched; the absence
            // is bought with the suite's settle, as every other "and nothing further" is.
            await IdleAsync(ana);
            await IdleAsync(carlos);
            await Eventually.Settle();
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
            await OpenTopicPastTheOverlayAsync(fresh, topic);
            await UserMessage(fresh, $"{topic}: IMPORTANT").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await Assertions.Expect(UserMessage(fresh, $"ALPHA{tag}")).ToHaveCountAsync(0);
            await Assertions.Expect(UserMessage(fresh, $"CHARLIE{tag}")).ToHaveCountAsync(0);
        }
        finally
        {
            await DrainPendingApprovalsAsync(ana);
        }
    }
}