using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// Two people sending in a shared topic at the same moment put both messages on one stream. Each page
// reads either its own send's wire or a resume of the stream the other's message announced, and
// neither drew the other person's message: the resume drew only the stream's one current prompt and
// a send's wire dropped people's messages. Ana saw the reply to Carlos's message, never the message,
// until a reload. Several rounds, both orders, since which page wins is the race.
[Collection(WebChatE2ECollections.People)]
[Trait("Category", "E2E")]
public partial class SharedTopicSimultaneousSendE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoPeopleSendingAtOnceInASharedTopic_EachSeesTheOthersMessage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var ana = await fixture.CreatePageSignedInAsync((await PersonAsync($"Ana {tag}")).Session);
        await using var carlosContext = await ContextOfAsync(ana, (await PersonAsync($"Carlos {tag}")).Session);

        await WebChatE2ETests.GotoWebChatAsync(ana, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(ana);
        await SendAsync(ana, $"Plan {tag}: answer only OK.");
        var topic = $"Plan {tag}";
        await ana.Locator(".topic-item", new PageLocatorOptions { HasText = topic })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await IdleAsync(ana);
        await ana.Locator("[title*='tap to share'], [aria-label*='tap to share']").First
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        var carlos = await carlosContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(carlos, fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(carlos);
        await carlos.Locator(".topic-item", new PageLocatorOptions { HasText = topic })
            .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await Assertions.Expect(Shown(carlos, $"Plan {tag}: answer only OK."))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        foreach (var round in Enumerable.Range(1, 3))
        {
            var fromAna = $"Ana {tag} round {round}: answer only OK.";
            var fromCarlos = $"Carlos {tag} round {round}: answer only OK.";
            await Task.WhenAll(round % 2 == 1
                ? [SendAsync(ana, fromAna), SendAsync(carlos, fromCarlos)]
                : [SendAsync(carlos, fromCarlos), SendAsync(ana, fromAna)]);

            foreach (var page in new[] { ana, carlos })
            {
                foreach (var words in new[] { fromAna, fromCarlos })
                {
                    await Assertions.Expect(Shown(page, words))
                        .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });
                }
            }

            await Task.WhenAll(IdleAsync(ana), IdleAsync(carlos));
        }
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