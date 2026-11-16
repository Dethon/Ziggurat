using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.QueueE2E;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Messages sent while a reply runs wait in the topic's queue, shown as chips above the composer, and
// run one after the other once it ends — against the stack's real model. The queue is the topic's, so
// the person's other device opening the topic shows the same chips, and another member reading it shared
// sees them named and read-only. The running reply is one held on a tool's approval, so it is still
// running however long the queueing takes.
[Collection(WebChatE2ECollections.Queue)]
[Trait("Category", "E2E")]
public class QueuedMessagesE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoMessagesQueuedBehindAToolUsingReply_ShowAsChipsOnEveryPage_AndRunInOrder()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var input = page.Locator("textarea.chat-input");
        IPage? other = null;

        try
        {
            await HoldOnAnApprovalAsync(page, $"Queue {tag}: {ToolDemand}");

            // Sent while the reply waits on its approval: the composer still sends, with Stop beside
            // Send rather than in its place, and nothing is drawn.
            await input.FillAsync($"Reply with only the word ALPHA{tag}. Do not call any tools.");
            await Assertions.Expect(page.Locator("[data-testid=composer-send]")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-testid=composer-cancel]")).ToBeVisibleAsync();
            await input.PressAsync("Enter");
            await SendAsync(page, $"Reply with only the word BRAVO{tag}. Do not call any tools.");

            var chips = page.Locator("[data-testid=queue-chip]");
            await Assertions.Expect(chips).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(chips.Nth(0)).ToContainTextAsync($"ALPHA{tag}");
            await Assertions.Expect(chips.Nth(1)).ToContainTextAsync($"BRAVO{tag}");
            await Assertions.Expect(UserMessage(page, $"ALPHA{tag}")).ToHaveCountAsync(0);

            // The person's other device, opening the topic only now that both wait, asks for the queue
            // and shows the same chips, theirs to edit there too. It is offered the held approval as
            // it connects, so it neither goes through SelectAgentAsync, which turns overlays down,
            // nor clicks through the overlay.
            other = await page.Context.NewPageAsync();
            await GotoWebChatAsync(other, fixture.WebChatUrl);
            await OpenTopicPastTheOverlayAsync(other, $"Queue {tag}");
            var otherChips = other.Locator("[data-testid=queue-chip]");
            await Assertions.Expect(otherChips).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(otherChips.Nth(0)).ToContainTextAsync($"ALPHA{tag}");
            await Assertions.Expect(otherChips.Nth(1)).ToContainTextAsync($"BRAVO{tag}");
            await Assertions.Expect(other.Locator("[data-testid=queue-chip-edit]")).ToHaveCountAsync(2);

            await page.Locator(".btn-approve").ClickAsync();

            // The tool's reply ends, then each queued message runs in turn; any further approval the
            // model asks for is turned down, which only ends that turn sooner.
            await WaitForAsync(page, () => AnswerTo(page, $"BRAVO{tag}"), "the second queued message to be answered");
            await Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
                .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 180_000 });
            await Assertions.Expect(chips).ToHaveCountAsync(0);
            await Assertions.Expect(other.Locator("[data-testid=queue-chip]")).ToHaveCountAsync(0);

            var said = await Conversation(page);
            var alpha = said.IndexOf($"user: Reply with only the word ALPHA{tag}. Do not call any tools.");
            var bravo = said.IndexOf($"user: Reply with only the word BRAVO{tag}. Do not call any tools.");
            alpha.ShouldBeGreaterThan(0, string.Join("\n", said));
            bravo.ShouldBeGreaterThan(alpha, string.Join("\n", said));
            said.Count(line => line.Contains($"word ALPHA{tag}")).ShouldBe(1);
            said.Count(line => line.Contains($"word BRAVO{tag}")).ShouldBe(1);
            said.Skip(alpha + 1).Take(bravo - alpha - 1).ShouldContain(line => line.StartsWith("assistant:") && line.Contains($"ALPHA{tag}"));
            said.Skip(bravo + 1).ShouldContain(line => line.StartsWith("assistant:") && line.Contains($"BRAVO{tag}"));
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
            if (other is not null)
            {
                await other.CloseAsync();
            }
        }
    }

    // The queue is the topic's, so another member reading a shared topic sees what its owner queued: the
    // chip named by its sender, and theirs alone to edit, remove or steer.
    [SkippableFact]
    public async Task AMembersQueuedMessage_ShowsOnAnotherMembersPage_NamedAndWithoutActions()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var ana = await fixture.CreatePageSignedInAsync((await fixture.PersonAsync($"Ana {tag}", "member")).Session);
        await using var beaContext = await ContextOfAsync(
            ana, (await fixture.PersonAsync($"Bea {tag}", "member")).Session, fixture.WebChatUrl);
        var anaWords = $"Reply with only the word ALPHA{tag}. Do not call any tools.";

        await GotoWebChatAsync(ana, fixture.WebChatUrl);
        await SelectAgentAsync(ana);
        var topic = $"Shared queue {tag}";
        var bea = await beaContext.NewPageAsync();

        try
        {
            await HoldOnAnApprovalAsync(ana, $"{topic}: {ToolDemand}");

            // Shared while its reply waits on the approval, whose overlay covers the header: the press is
            // dispatched on the toggle itself.
            await ana.Locator("[title*='tap to share']").First.DispatchEventAsync("click");
            await Assertions.Expect(ana.Locator("[title*='tap to make it yours alone']"))
                .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

            await GotoWebChatAsync(bea, fixture.WebChatUrl);
            await SelectAgentAsync(bea);
            await OpenTopicPastTheOverlayAsync(bea, topic);
            await Assertions.Expect(bea.Locator("[data-testid=composer-cancel]"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

            await SendAsync(ana, anaWords);

            var anaChip = ana.Locator("[data-testid=queue-chip]", new PageLocatorOptions { HasText = $"ALPHA{tag}" });
            await Assertions.Expect(anaChip).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(anaChip.Locator("[data-testid=queue-chip-edit]")).ToHaveCountAsync(1);

            var beaChip = bea.Locator("[data-testid=queue-chip]", new PageLocatorOptions { HasText = $"ALPHA{tag}" });
            await Assertions.Expect(beaChip).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(beaChip.Locator(".queue-chip-sender")).ToHaveTextAsync($"Ana {tag}");
            await Assertions.Expect(beaChip.Locator("[data-testid=queue-chip-edit]")).ToHaveCountAsync(0);
            await Assertions.Expect(beaChip.Locator("[data-testid=queue-chip-remove]")).ToHaveCountAsync(0);
            await Assertions.Expect(beaChip.Locator("[data-testid=queue-chip-steer]")).ToHaveCountAsync(0);

            // Stopped rather than approved, so the queued message goes back to Ana instead of costing a
            // reply. The approval's overlay covers the composer, so the press is dispatched on Stop itself.
            await ana.Locator("[data-testid=composer-cancel]").DispatchEventAsync("click");
            await IdleAsync(ana);
            await IdleAsync(bea);
        }
        finally
        {
            await DrainPendingApprovalsAsync(ana);
        }
    }
}