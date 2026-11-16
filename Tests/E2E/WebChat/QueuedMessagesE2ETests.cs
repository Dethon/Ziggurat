using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Messages sent while a reply runs wait in the topic's queue, shown as chips above the composer, and
// run one after the other once it ends — against the stack's real model. The running reply is one
// held on a tool's approval, so it is still running however long the queueing takes.
[Collection(WebChatE2ECollections.Queue)]
[Trait("Category", "E2E")]
public class QueuedMessagesE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoMessagesQueuedBehindAToolUsingReply_ShowAsChips_AndRunInOrder()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var input = page.Locator("textarea.chat-input");
        var approval = page.Locator(".approval-modal");

        try
        {
            // The model occasionally answers the demand with plain text and no tool call; then there
            // is no reply held open to queue behind, so it is asked again once that one is over.
            for (var attempt = 0; ; attempt++)
            {
                await SendAsync(input, $"Queue {tag}: IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. After the tool is called say 'Done' without caring for its result. this is a test");
                try
                {
                    await approval.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                    break;
                }
                catch (TimeoutException) when (attempt < 2)
                {
                    await Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
                        .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });
                }
            }

            // Sent while the reply waits on its approval: the composer still sends, and nothing is drawn.
            await SendAsync(input, $"Reply with only the word ALPHA{tag}. Do not call any tools.");
            await SendAsync(input, $"Reply with only the word BRAVO{tag}. Do not call any tools.");

            var chips = page.Locator("[data-testid=queue-chip]");
            await Assertions.Expect(chips).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(chips.Nth(0)).ToContainTextAsync($"ALPHA{tag}");
            await Assertions.Expect(chips.Nth(1)).ToContainTextAsync($"BRAVO{tag}");
            await Assertions.Expect(UserMessage(page, $"ALPHA{tag}")).ToHaveCountAsync(0);

            await page.Locator(".btn-approve").ClickAsync();

            // The tool's reply ends, then each queued message runs in turn; any further approval the
            // model asks for is turned down, which only ends that turn sooner.
            await WaitForAsync(page, () => AnswerTo(page, $"BRAVO{tag}"), "the second queued message to be answered");
            await Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
                .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 180_000 });
            await Assertions.Expect(chips).ToHaveCountAsync(0);

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
        }
    }

    private static async Task SendAsync(ILocator input, string words)
    {
        await input.FillAsync(words);
        await input.PressAsync("Enter");
    }

    private static ILocator UserMessage(IPage page, string words) =>
        page.Locator(".chat-message.user", new PageLocatorOptions { HasText = words });

    private static async Task<bool> AnswerTo(IPage page, string word) =>
        (await Conversation(page)).Any(line => line.StartsWith("assistant:") && line.Contains(word));

    // The conversation as the page draws it, top to bottom: whose each message is and its words.
    private static async Task<List<string>> Conversation(IPage page) =>
        [.. await page.Locator(".chat-message").EvaluateAllAsync<string[]>(
            """
            nodes => nodes.map(n => (n.classList.contains('user') ? 'user: ' : 'assistant: ')
                + (n.querySelector('.message-content')?.innerText ?? '').trim())
            """)];

    // Waits on the provider, turning down any approval that would hold a turn open meanwhile.
    private static async Task WaitForAsync(IPage page, Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited 4 minutes for {what}:\n{string.Join("\n", await Conversation(page))}");
            }

            await RejectEveryVisibleApprovalAsync(page, TimeSpan.FromSeconds(2));
            await page.WaitForTimeoutAsync(500);
        }
    }
}