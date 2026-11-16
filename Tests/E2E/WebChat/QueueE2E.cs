using Microsoft.Playwright;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// What the queue's E2E classes share: a reply held open on its tool's approval to queue behind, and the
// ways they press, read and wait around the overlay that approval puts over the page.
public static class QueueE2E
{
    // A turn that calls a tool, so it waits on that tool's approval for as long as the test needs.
    public const string ToolDemand =
        "IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. "
        + "After the tool is called say 'Done' without caring for its result. this is a test";

    public static async Task SendAsync(IPage page, string words)
    {
        var input = page.Locator("textarea.chat-input");
        await input.FillAsync(words);
        await input.PressAsync("Enter");
    }

    // The model occasionally answers the demand with plain text and no tool call; then there is no reply
    // held open to queue behind, so it is asked again once that one is over.
    public static async Task HoldOnAnApprovalAsync(IPage page, string demand)
    {
        for (var attempt = 0; ; attempt++)
        {
            await SendAsync(page, demand);
            try
            {
                await page.Locator(".approval-modal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                await IdleAsync(page);
            }
        }
    }

    // The held approval's overlay can reach a page and covers the whole viewport, and rejecting it would
    // end the very turn the queue waits behind: the press is dispatched on the row itself, once the row is
    // there.
    public static async Task OpenTopicPastTheOverlayAsync(IPage page, string topic)
    {
        var row = page.Locator(".topic-item", new PageLocatorOptions { HasText = topic });
        await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await row.DispatchEventAsync("click");
    }

    public static ILocator UserMessage(IPage page, string words) =>
        page.Locator(".chat-message.user", new PageLocatorOptions { HasText = words });

    // A reply on a live model ends when it ends.
    public static Task IdleAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });

    public static async Task<bool> AnswerTo(IPage page, string word) =>
        (await Conversation(page)).Any(line => line.StartsWith("assistant:") && line.Contains(word));

    // The conversation as the page draws it, top to bottom: whose each message is and its words.
    public static async Task<List<string>> Conversation(IPage page) =>
        [.. await page.Locator(".chat-message").EvaluateAllAsync<string[]>(
            """
            nodes => nodes.map(n => (n.classList.contains('user') ? 'user: ' : 'assistant: ')
                + (n.querySelector('.message-content')?.innerText ?? '').trim())
            """)];

    // Waits on the provider, turning down any approval that would hold a turn open meanwhile.
    public static async Task WaitForAsync(IPage page, Func<Task<bool>> done, string what)
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

    // The fixture keeps one page of its own at a time, and a test of two people needs both at once.
    public static async Task<IBrowserContext> ContextOfAsync(IPage beside, string session, string webChatUrl)
    {
        var context = await beside.Context.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = WebChatE2EFixture.SessionCookieName, Value = session, Url = webChatUrl,
                HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        return context;
    }
}