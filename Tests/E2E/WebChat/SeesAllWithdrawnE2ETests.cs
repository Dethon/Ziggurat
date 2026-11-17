using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// A member granted every topic of the household had another person's private one open when the grant
// was withdrawn: the list lost it and the hub refused its history, but the conversation stayed on their
// screen. A withdrawal reaches the open page at once and closes what it may no longer see.
//
// Its people are the stack's second household's (PersonElsewhereAsync): a grant has every open page of
// the household renew its connection, which in the home caught other collections mid-way. That
// household runs the home's agents on a key of its own; its owner goes to the members page directly.
[Collection(WebChatE2ECollections.Sharing)]
[Trait("Category", "E2E")]
public partial class SeesAllWithdrawnE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AWithdrawnGrant_ClosesTheOtherPersonsTopicTheMemberHadOpen()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var reader = $"Reader {tag}";
        var owner = await fixture.CreatePageSignedInAsync((await PersonAsync($"Owner {tag}", "owner")).Session);
        await using var writerContext = await ContextOfAsync(owner, (await PersonAsync($"Writer {tag}", "member")).Session);
        await using var readerContext = await ContextOfAsync(owner, (await PersonAsync(reader, "member")).Session);

        var writer = await writerContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(writer, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(writer);
        var words = $"Private {tag}: answer in one short sentence.";
        await SendAsync(writer, words);
        await writer.Locator(".topic-item", new PageLocatorOptions { HasText = $"Private {tag}" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await SetSeesAllAsync(owner, reader, true);

        var page = await readerContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        // Someone who sees all reads their own list until they ask for another person's.
        var whose = page.GetByLabel("Whose conversations");
        await Assertions.Expect(whose).ToHaveValueAsync("mine", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        var row = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Private {tag}" });
        await Assertions.Expect(row).ToHaveCountAsync(0);
        await Assertions.Expect(whose.Locator("option", new LocatorLocatorOptions { HasText = $"Writer {tag}'s" }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await whose.SelectOptionAsync(new SelectOptionValue { Label = $"Writer {tag}'s" });

        await row.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        var shown = page.Locator(".chat-message", new PageLocatorOptions { HasText = words });
        await Assertions.Expect(shown).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await SetSeesAllAsync(owner, reader, false);

        await Assertions.Expect(row).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
        await Assertions.Expect(shown).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
    }

    // A conversation started while the list shows another person's is the starter's own: the list
    // moves to theirs and holds its row, rather than leave it in the other person's list.
    [SkippableFact]
    public async Task ANewConversation_StartedWhileListingAnotherPersons_MovesTheListToMine()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var reader = $"Reader {tag}";
        var owner = await fixture.CreatePageSignedInAsync((await PersonAsync($"Owner {tag}", "owner")).Session);
        await PersonAsync($"Writer {tag}", "member");
        await using var readerContext = await ContextOfAsync(owner, (await PersonAsync(reader, "member")).Session);
        await SetSeesAllAsync(owner, reader, true);

        var page = await readerContext.NewPageAsync();
        var heard = await HeardOnAsync(page);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);
        var whose = page.GetByLabel("Whose conversations");
        await Assertions.Expect(whose.Locator("option", new LocatorLocatorOptions { HasText = $"Writer {tag}'s" }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await whose.SelectOptionAsync(new SelectOptionValue { Label = $"Writer {tag}'s" });

        heard.Mark("send");
        await SendAsync(page, $"Mine {tag}: answer in one short sentence.");

        await ExpectOrDescribeAsync(page, heard,
            () => Assertions.Expect(whose).ToHaveValueAsync("mine", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 }));
        await page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Mine {tag}" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    // Sent the way a person sends, not pressed blind. Every page of this household renews its connection
    // whenever anybody's role or grant changes in it — siblings in other collections change them at any
    // moment — and the composer is disabled while that lasts: an Enter pressed into it then reaches
    // nothing, and the words simply stay put. Send waits for a control that can take them (through any
    // approval a sibling's reply leaked over the page); and since a renewal can still start between that
    // check and the click, the click is made again while the composer visibly holds the words. It never
    // sends twice: a click that was taken emptied the composer first, and one on a render not caught up
    // yet finds nothing to send.
    private static async Task SendAsync(IPage page, string words)
    {
        var input = page.Locator("textarea.chat-input");
        var send = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send", Exact = true });
        await input.FillAsync(words);
        foreach (var attempt in Enumerable.Range(1, 3))
        {
            await WebChatE2ETests.ClickThroughApprovalsAsync(page, send);
            try
            {
                await Assertions.Expect(input).ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions { Timeout = 5_000 });
                return;
            }
            catch (PlaywrightException) when (attempt < 3)
            {
                var held = await input.InputValueAsync();
                if (held == "")
                {
                    return;
                }

                if (held != words)
                {
                    throw;
                }
            }
        }
    }

    // What the page held when the list did not move: whether the words are still in the composer (the
    // send never left) or gone (it left and died), what it was told, and when the household's access
    // changes reached it — each renews the page's connection, and siblings in other collections make
    // them in this household at any moment.
    private static async Task ExpectOrDescribeAsync(IPage page, PageLog heard, Func<Task> expectation)
    {
        try
        {
            await expectation();
        }
        catch (PlaywrightException failure)
        {
            throw new PlaywrightException($"{failure.Message}\n--- what the page held ---\n{await DescribeAsync(page, heard)}", failure);
        }
    }

    private static async Task<string> DescribeAsync(IPage page, PageLog heard)
    {
        var quick = new LocatorInputValueOptions { Timeout = 2_000 };
        async Task<string> Read(Func<Task<string>> read)
        {
            try
            {
                return await read();
            }
            catch (Exception ex)
            {
                return $"(unread: {ex.GetType().Name})";
            }
        }

        var composer = page.Locator("textarea.chat-input");
        return string.Join("\n",
            $"composer: '{await Read(() => composer.InputValueAsync(quick))}', disabled: {await Read(async () => (await composer.IsDisabledAsync(new LocatorIsDisabledOptions { Timeout = 2_000 })).ToString())}",
            $"filter: {await Read(() => page.GetByLabel("Whose conversations").InputValueAsync(quick))}",
            $"connection: {await Read(async () => (await page.Locator(".connection-status").AllInnerTextsAsync()).FirstOrDefault() ?? "")}",
            $"toasts: [{await Read(async () => string.Join(" | ", await page.Locator(".toast-message").AllInnerTextsAsync()))}]",
            $"rows: [{await Read(async () => string.Join(" | ", await page.Locator(".topic-item").AllInnerTextsAsync()))}]",
            $"timeline:\n  {string.Join("\n  ", heard.Lines)}");
    }

    private static async Task<PageLog> HeardOnAsync(IPage page)
    {
        var log = new PageLog();

        // The composer's connection, as the page drew it: a renewal disables it while it lasts.
        await page.AddInitScriptAsync("""
            (() => {
                let last = '';
                new MutationObserver(() => {
                    const input = document.querySelector('textarea.chat-input');
                    const status = document.querySelector('.connection-status')?.textContent?.trim() ?? '';
                    const now = `composer ${input ? (input.disabled ? 'disabled' : 'enabled') : 'absent'}, ${status}`;
                    if (now !== last) {
                        last = now;
                        console.warn(`[e2e] ${now}`);
                    }
                }).observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
            })();
            """);
        page.WebSocket += (_, socket) => socket.FrameReceived += (_, frame) =>
        {
            if (frame.Text?.Contains("OnAccessChanged", StringComparison.Ordinal) == true)
            {
                log.Mark("OnAccessChanged");
            }
        };
        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                log.Mark($"console {message.Type}: {message.Text}");
            }
        };
        return log;
    }

    private sealed class PageLog
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public void Mark(string what)
        {
            lock (_gate)
            {
                _lines.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {what}");
            }
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }
    }

    private async Task SetSeesAllAsync(IPage owner, string name, bool seesAll)
    {
        if (!owner.Url.EndsWith("/admin/members", StringComparison.Ordinal))
        {
            await owner.GotoAsync(fixture.DefaultSpaceUrl + "admin/members");
            await owner.WaitForURLAsync(new Regex("/admin/members$"));
        }

        var row = owner.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await row.GetByLabel("Sees all conversations").SetCheckedAsync(seesAll, new LocatorSetCheckedOptions { Timeout = 60_000 });
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(owner.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
    }

    // The fixture keeps one page of its own at a time, and this test needs three people at once.
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

    private async Task<(string User, string Session)> PersonAsync(string name, string role)
    {
        return await fixture.PersonElsewhereAsync(name, role);
    }

}