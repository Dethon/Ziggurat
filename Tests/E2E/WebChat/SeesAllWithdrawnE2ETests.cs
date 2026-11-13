using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// A member granted every topic of the household had another person's private one open when the grant
// was withdrawn: the list lost it and the hub refused its history, but the conversation stayed on their
// screen. A withdrawal reaches the open page at once and closes what it may no longer see.
[Collection(WebChatE2ECollections.People)]
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
        await writer.Locator("textarea.chat-input").FillAsync(words);
        await writer.Locator("textarea.chat-input").PressAsync("Enter");
        await writer.Locator(".topic-item", new PageLocatorOptions { HasText = $"Private {tag}" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await SetSeesAllAsync(owner, reader, true);

        var page = await readerContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);
        var row = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Private {tag}" });
        await row.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        var shown = page.Locator(".chat-message", new PageLocatorOptions { HasText = words });
        await Assertions.Expect(shown).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await SetSeesAllAsync(owner, reader, false);

        await Assertions.Expect(row).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
        await Assertions.Expect(shown).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
    }

    private async Task SetSeesAllAsync(IPage owner, string name, bool seesAll)
    {
        if (!owner.Url.EndsWith("/admin/members", StringComparison.Ordinal))
        {
            await WebChatE2ETests.GotoWebChatAsync(owner, fixture.WebChatUrl);
            await owner.GetByLabel("Household settings").ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await owner.WaitForURLAsync(new Regex("/admin/settings$"));
            await owner.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Members" }).ClickAsync();
            await owner.WaitForURLAsync(new Regex("/admin/members$"));
        }

        var row = owner.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await row.GetByLabel("Sees all conversations").SetCheckedAsync(seesAll, new LocatorSetCheckedOptions { Timeout = 30_000 });
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
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name, "--role", role);
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        return (user, (await fixture.AccountsAsync("mint-session", "--user", user)).Output.Trim());
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household")]
    private static partial Regex WhoWasMade();
}