using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// A device signed out from another one — a lost phone, say, signed out from the laptop's account
// page — stops being the person at once, not when it next tries to send: until it was told, its open
// chat stayed connected, was pushed the person's new private conversations and could open them.
[Collection(WebChatE2ECollections.People)]
[Trait("Category", "E2E")]
public partial class SignedOutElsewhereE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task ADeviceSignedOutFromAnother_LeavesTheChatForSignIn()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var (user, phoneSession) = await fixture.PersonAsync($"Person {tag}", "member");
        var phone = await fixture.CreatePageSignedInAsync(phoneSession);
        // The fixture keeps one page of its own at a time, and this test needs two devices at once:
        // the laptop is a context of the test's own on the same browser.
        await using var laptopContext = await phone.Context.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await laptopContext.AddCookiesAsync(
        [
            new Cookie
            {
                Name = WebChatE2EFixture.SessionCookieName, Value = await SessionAsync(user), Url = fixture.WebChatUrl,
                HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        var laptop = await laptopContext.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(phone, fixture.WebChatUrl);
        await Assertions.Expect(phone.GetByText("Connected", new PageGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });

        await laptop.GotoAsync(new Uri(new Uri(fixture.WebChatUrl), "/accounts/account").ToString());
        var otherDevice = laptop.Locator("li[data-session-id]").Filter(new LocatorFilterOptions { HasNotText = "(this device)" });
        await Assertions.Expect(otherDevice).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await otherDevice.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Sign out this device" }).ClickAsync();
        await Assertions.Expect(laptop.Locator("li[data-session-id]")).ToHaveCountAsync(1);

        await phone.WaitForURLAsync(new Regex("/accounts/sign-in"), new PageWaitForURLOptions { Timeout = 60_000 });
    }

    private async Task<string> SessionAsync(string user) =>
        (await fixture.AccountsAsync("mint-session", "--user", user)).Output.Trim();
}