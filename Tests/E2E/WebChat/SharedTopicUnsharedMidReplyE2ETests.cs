using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// Ana made a topic private while its reply streamed on Carlos's page: his page tried to drop it by
// asking the hub to stop Ana's reply, was refused, kept the row, and the hub kept sending him the reply
// as it was written. Once private, the topic and its reply leave his page at once and stay gone.
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class SharedTopicUnsharedMidReplyE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task ATopicMadePrivateMidReply_LeavesTheOtherMembersPage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var ana = await fixture.CreatePageSignedInAsync((await PersonAsync($"Ana {tag}")).Session);
        await using var carlosContext = await ContextOfAsync(ana, (await PersonAsync($"Carlos {tag}")).Session);

        await WebChatE2ETests.GotoWebChatAsync(ana, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(ana);
        await SendAsync(ana, $"Wine {tag}: answer only OK.");
        var row = $"Wine {tag}";
        await ana.Locator(".topic-item", new PageLocatorOptions { HasText = row })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await IdleAsync(ana);
        await ana.Locator("[title*='tap to share'], [aria-label*='tap to share']").First
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        var carlos = await carlosContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(carlos, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(carlos);
        var carlosRow = carlos.Locator(".topic-item", new PageLocatorOptions { HasText = row });
        await carlosRow.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });

        var question = $"Wine {tag} again: write ten long paragraphs about the history of wine.";
        await SendAsync(ana, question);
        await Assertions.Expect(carlos.Locator(".chat-message.streaming-cursor"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });
        await ana.Locator("[title*='tap to make it yours alone'], [aria-label*='tap to make it yours alone']").First.ClickAsync();

        await Assertions.Expect(carlosRow).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Assertions.Expect(Shown(carlos, question)).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Assertions.Expect(carlos.Locator(".chat-message.assistant")).ToHaveCountAsync(0);
        await IdleAsync(ana);
        await Assertions.Expect(carlos.Locator(".chat-message")).ToHaveCountAsync(0);
    }

    private static ILocator Shown(IPage page, string words) =>
        page.Locator(".chat-message.user", new PageLocatorOptions { HasText = words });

    private static async Task SendAsync(IPage page, string words)
    {
        await page.Locator("textarea.chat-input").FillAsync(words);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send", Exact = true })
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
    }

    // A reply on a live model ends when it ends; a page cannot send in a topic while one streams.
    private static Task IdleAsync(IPage page) =>
        Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Stop the reply" }))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 120_000 });

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

    private async Task<(string User, string Session)> PersonAsync(string name)
    {
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name, "--role", "member");
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        return (user, (await fixture.AccountsAsync("mint-session", "--user", user)).Output.Trim());
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household")]
    private static partial Regex WhoWasMade();
}