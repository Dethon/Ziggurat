using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// One phone signed in as one person after another: Eva described a meal and left it unsaved, the
// phone was signed in as Dani, and Dani's app showed Eva's words — and would have shown her meal's
// thumbnail and the insulin she typed. The meal she has not saved is kept on the phone for her alone.
// No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugarySharedPhoneE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AMealOnePersonLeftUnsaved_IsNotShownToTheNextPersonOnThePhone()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var eva = await fixture.AccountForAsync(fixture.NextUserIndex());
        var dani = await fixture.AccountForAsync(fixture.NextUserIndex());
        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var phone = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(eva)
        });
        var page = await phone.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        var words = page.GetByPlaceholder("Optional. For example", new PageGetByPlaceholderOptions { Exact = false });
        await words.FillAsync($"Eva {tag}: tarta a escondidas", new LocatorFillOptions { Timeout = 60_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Estimate carbs" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // Back as Eva first: her own unsaved meal is still hers.
        await page.ReloadAsync();
        await Assertions.Expect(words).ToHaveValueAsync($"Eva {tag}: tarta a escondidas", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });

        await phone.SetExtraHTTPHeadersAsync(Headers(dani));
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Take a photo" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(words).ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions { Timeout = 10_000 });
        await Assertions.Expect(page.GetByText($"Eva {tag}")).ToHaveCountAsync(0);
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}