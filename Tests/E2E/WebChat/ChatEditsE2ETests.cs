using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Rewriting a conversation from its page against the stack's real model: acted on straight after a
// reply streamed, with no reload, since that is when a person reaches for it. A collection of its own:
// its turns are the provider's seconds, and queued behind the chat's other real-model turns they were
// the run's longest chain by forty seconds.
[Collection(WebChatE2ECollections.Rewrites)]
[Trait("Category", "E2E")]
public class ChatEditsE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AReplyJustStreamed_IsRerolled_AndEitherVersionIsShown()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await OpenAsync();

        await SendAsync(page, $"Reroll {tag}: reply with one short sentence about the sea. Do not call any tools.");
        await RepliesAsync(page, 1);

        await ChooseAsync(Replies(page).Last, "Reroll");
        await Assertions.Expect(Replies(page).Last.Locator(".version-position"))
            .ToHaveTextAsync("2/2", new LocatorAssertionsToHaveTextOptions { Timeout = 180_000 });
        await IdleAsync(page);

        await Replies(page).Last.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Previous version" }).ClickAsync();
        await Assertions.Expect(Replies(page).Last.Locator(".version-position"))
            .ToHaveTextAsync("1/2", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
    }

    [SkippableFact]
    public async Task AnEditedMessage_IsRunAgain_AndWhatItReplacedIsAVersion()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await OpenAsync();

        await SendAsync(page, $"Edit {tag}: answer only OK. Do not call any tools.");
        await RepliesAsync(page, 1);

        var asked = page.Locator(".chat-message.user").Last;
        await ChooseAsync(asked, "Edit");
        await page.Locator(".message-editor-text").FillAsync($"Edited {tag}: answer only YES. Do not call any tools.");
        await page.Locator(".message-editor-button.primary").ClickAsync();
        if (await page.Locator(".message-editor-warning").IsVisibleAsync())
        {
            await page.Locator(".message-editor-button.primary").ClickAsync();
        }

        var edited = page.Locator(".chat-message.user", new PageLocatorOptions { HasText = $"Edited {tag}" });
        await Assertions.Expect(edited.Locator(".version-position"))
            .ToHaveTextAsync("2/2", new LocatorAssertionsToHaveTextOptions { Timeout = 180_000 });
        await RepliesAsync(page, 1);
    }

    [SkippableFact]
    public async Task DeletingAReply_TakesItOffEveryPageOnTheTopic()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var (_, session) = await fixture.PersonAsync($"Editor {tag}", "member");
        var page = await fixture.CreatePageSignedInAsync(session);
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);

        await SendAsync(page, $"Delete {tag}: answer only OK. Do not call any tools.");
        await RepliesAsync(page, 1);

        // The same person's other device: the fixture keeps one page of its own, so this one is a
        // context of the test's, signed in by the same session.
        await using var elsewhere = await page.Context.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await elsewhere.AddCookiesAsync(
        [
            new Cookie
            {
                Name = WebChatE2EFixture.SessionCookieName, Value = session, Url = fixture.WebChatUrl,
                HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        var other = await elsewhere.NewPageAsync();
        await GotoWebChatAsync(other, fixture.WebChatUrl);
        await ClickThroughApprovalsAsync(other, other.Locator(".topic-item", new PageLocatorOptions { HasText = $"Delete {tag}" }).First);
        await Assertions.Expect(Replies(other)).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await ChooseAsync(Replies(page).Last, "Delete from here", confirm: "Delete");

        await Assertions.Expect(Replies(page)).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Assertions.Expect(Replies(other)).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Assertions.Expect(other.Locator(".chat-message.user", new PageLocatorOptions { HasText = $"Delete {tag}" }))
            .ToHaveCountAsync(1);
    }

    [SkippableFact]
    public async Task AForkFromAReply_OpensANewTopicHoldingTheLineSoFar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await OpenAsync();

        await SendAsync(page, $"Fork {tag}: answer only OK. Do not call any tools.");
        await RepliesAsync(page, 1);

        await ChooseAsync(Replies(page).Last, "Fork from here");

        await page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Fork {tag}" })
            .Filter(new LocatorFilterOptions { HasText = "(fork)" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".chat-message.user", new PageLocatorOptions { HasText = $"Fork {tag}" }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Assertions.Expect(Replies(page)).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
    }

    // Stopping a reply ends its turn: the conversation can be rewritten at once, not once the turn's
    // in-flight mark expires.
    [SkippableFact]
    public async Task AfterAStop_TheConversationCanBeRewrittenAtOnce()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await OpenAsync();

        await SendAsync(page, $"Stop {tag}: answer only OK. Do not call any tools.");
        await RepliesAsync(page, 1);
        await SendAsync(page, "Write a very long and detailed story about a space adventure. Do not call any tools.");
        var stop = page.Locator("[data-testid=composer-cancel]");
        await stop.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // Stopped once the reply is under way: a turn stopped before the agent began it keeps nothing.
        await Assertions.Expect(page.Locator(".chat-message.assistant .message-content")).ToHaveCountAsync(2,
            new LocatorAssertionsToHaveCountOptions { Timeout = 120_000 });
        await ClickThroughApprovalsAsync(page, stop);
        await IdleAsync(page);

        // The agent keeps the stopped turn after the stop ended its stream; once its message is named,
        // the turn is kept and over.
        await Assertions.Expect(page.Locator(".chat-message.user", new PageLocatorOptions { HasText = "lighthouse" })
                .Or(page.Locator(".chat-message.user", new PageLocatorOptions { HasText = "space adventure" }))
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Message actions" }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await ChooseAsync(page.Locator(".chat-message.user", new PageLocatorOptions { HasText = $"Stop {tag}" }), "Delete from here",
            confirm: "Delete");

        await Assertions.Expect(page.Locator(".chat-message.user", new PageLocatorOptions { HasText = $"Stop {tag}" }))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".toast", new PageLocatorOptions { HasText = "still being written" })).ToHaveCountAsync(0);
    }

    // A reply the person stopped is kept as far as it got, and Continue carries it on in the same bubble.
    [SkippableFact]
    public async Task AStoppedReply_IsKept_AndContinuedInTheSameBubble()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var page = await OpenAsync();

        await SendAsync(page, "Write a very long and detailed story about a lighthouse keeper. Do not call any tools.");
        await WebChatConversationE2ETests.WaitForAssistantTextAsync(page);
        var stop = page.Locator("[data-testid=composer-cancel]");
        await ClickThroughApprovalsAsync(page, stop);
        await IdleAsync(page);

        var reply = Replies(page).Last;
        await Assertions.Expect(reply.Locator(".message-interrupted")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        var before = (await reply.Locator(".message-content").InnerTextAsync()).Length;

        await ChooseAsync(reply, "Continue");
        await Assertions.Expect(stop).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });

        // Grown past what was kept is the claim; the rest of a very long story is the provider's half
        // minute, not the test's, so the continuation is stopped once it has added to the bubble.
        await GrowsPastAsync(Replies(page).Last.Locator(".message-content"), before);
        if (await stop.IsVisibleAsync())
        {
            await ClickThroughApprovalsAsync(page, stop);
        }

        await IdleAsync(page);

        await Assertions.Expect(Replies(page)).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".chat-message.user")).ToHaveCountAsync(1);
        var after = (await Replies(page).Last.Locator(".message-content").InnerTextAsync()).Length;
        after.ShouldBeGreaterThan(before);
    }

    private static async Task GrowsPastAsync(ILocator content, int length)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while ((await content.InnerTextAsync()).Length <= length)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The continued reply never grew past its {length} kept characters in 2 minutes.");
            }

            await content.Page.WaitForTimeoutAsync(250);
        }
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        return page;
    }

    private static ILocator Replies(IPage page) =>
        page.Locator(".chat-message.assistant:not(.error)", new PageLocatorOptions { Has = page.Locator(".message-content") });

    private static async Task SendAsync(IPage page, string words)
    {
        var input = page.Locator("textarea.chat-input");
        await input.FillAsync(words);
        await input.PressAsync("Enter");
    }

    // The bubble's actions are one tap target, then the menu's item, then — for what discards or runs
    // tools again — the menu's own confirmation.
    private static async Task ChooseAsync(ILocator bubble, string item, string? confirm = null)
    {
        var page = bubble.Page;
        await ClickThroughApprovalsAsync(page, bubble.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Message actions" }));
        await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = item, Exact = true }).ClickAsync();
        var confirmation = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = confirm ?? "Run it again", Exact = true });
        if (confirm is not null || await confirmation.IsVisibleAsync())
        {
            await confirmation.ClickAsync();
        }
    }

    // A real model's reply, its tool approvals turned down as they come; then the composer idle again.
    private static async Task RepliesAsync(IPage page, int count)
    {
        await WebChatConversationE2ETests.WaitForAssistantTextAsync(page);
        await IdleAsync(page);
        await Assertions.Expect(Replies(page)).ToHaveCountAsync(count, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        // The bubbles are named once the page has read the stored turn; until then they have no actions.
        await Assertions.Expect(Replies(page).Last.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Message actions" }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
    }

    private static Task IdleAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 180_000 });
}