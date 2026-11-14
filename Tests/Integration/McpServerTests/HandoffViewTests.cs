using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Browser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;
using static Tests.Integration.McpServerTests.WebIdentityServerHarness;

namespace Tests.Integration.McpServerTests;

// The live view as a phone meets it, most often inside the installed chat app, which has no address
// bar and no Back button: the site drawn at the phone's size, a tap that clicks and a drag that
// scrolls, the keyboard a field wants, a view that survives the phone leaving for its authenticator
// app, and a way back to the chat once it is done. A real Chromium on the page's own script, against a
// browser that only records what reaches the tab.
public sealed class HandoffViewTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly FakeHandoffBrowser _browser = new();
    private WebIdentityServerHarness _server = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _chromium = null!;

    // One key for the class, and nobody's logins remembered as each test starts: a Done saves them.
    private static readonly string StateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public async Task InitializeAsync()
    {
        await redis.Connection.GetDatabase().KeyDeleteAsync([$"websearch:person:{Fran.UserId}"]);
        _server = await StartAsync(
            configure: services =>
            {
                services.RemoveAll<IHandoffBrowser>();
                services.AddSingleton<IHandoffBrowser>(_browser);
                services.RemoveAll<IPersonContexts>();
                services.AddSingleton<IPersonContexts>(_browser);
            },
            settings: s => s with
            {
                RedisConnectionString = redis.ConnectionString,
                Handoff = s.Handoff with { PublicBaseUrl = "https://assistants.test" },
                Logins = s.Logins with { StateKey = StateKey }
            });
        _playwright = await Playwright.CreateAsync();
        _chromium = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
        });
        _browser.Frame = await PictureAsync();
    }

    public async Task DisposeAsync()
    {
        await _chromium.DisposeAsync();
        _playwright.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task OnAPhone_TheSiteIsDrawnAtThePhonesSize()
    {
        var page = await PhoneAsync();

        await page.GotoAsync(await LinkAsync(FromChatClient(Fran)));

        await Eventually.Until(() => Inputs.OfType<HandoffInput.Resize>().Any(), "the view to say its size");
        var size = Inputs.OfType<HandoffInput.Resize>().First();
        size.Width.ShouldBeInRange(340, 390);
        size.Height.ShouldBeGreaterThan(450);
    }

    // A finger on the picture was a mouse held down: a drag selected text on the site and nothing
    // scrolled it. A tap clicks where it lands; a drag scrolls the site under it.
    [Fact]
    public async Task ATap_ClicksWhereItLands_AndADrag_ScrollsTheSite()
    {
        var page = await OpenAsync(FromChatClient(Fran));
        var box = (await page.Locator("#frame").BoundingBoxAsync())!;

        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        await Eventually.Until(() => Inputs.OfType<HandoffInput.MouseUp>().Any(), "the tap to click");
        Inputs.OfType<HandoffInput.MouseDown>().Single().X.ShouldBe(0.5, 0.02);
        Inputs.OfType<HandoffInput.Wheel>().ShouldBeEmpty();

        await DragAsync(page, box.X + box.Width / 2, box.Y + box.Height * 0.8, box.Y + box.Height * 0.3);

        await Eventually.Until(() => Inputs.OfType<HandoffInput.Wheel>().Sum(w => w.DeltaY) > 100,
            $"the drag to scroll the site down (scrolled {Inputs.OfType<HandoffInput.Wheel>().Sum(w => w.DeltaY)})");
        Inputs.OfType<HandoffInput.MouseDown>().Count().ShouldBe(1, "a drag clicks nothing");
    }

    // A tap on the site's email field brings up the phone's email keyboard at once; what is typed goes to
    // the site. Before, the field took the click and the keyboard waited for a second tap on a box below.
    [Fact]
    public async Task ATapOnAField_BringsUpTheKeyboardItWants_AndTheTypingReachesTheSite()
    {
        _browser.Fields = [new HandoffField(0.1, 0.4, 0.8, 0.2, "email", Focused: false)];
        var page = await OpenAsync(FromChatClient(Fran));
        var box = (await page.Locator("#frame").BoundingBoxAsync())!;

        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height / 2);

        await Assertions.Expect(page.Locator("#keys")).ToBeFocusedAsync();
        (await page.Locator("#keys").GetAttributeAsync("inputmode")).ShouldBe("email");
        await page.Keyboard.TypeAsync("fran@example.test");
        await Eventually.Until(() => string.Concat(Inputs.OfType<HandoffInput.Text>().Select(t => t.Value)) == "fran@example.test",
            "the typing to reach the site");
    }

    [Fact]
    public async Task ATapBesideEveryField_LeavesTheKeyboardDown()
    {
        _browser.Fields = [new HandoffField(0.1, 0.1, 0.8, 0.1, "email", Focused: false)];
        var page = await OpenAsync(FromChatClient(Fran));
        var box = (await page.Locator("#frame").BoundingBoxAsync())!;

        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height * 0.8f);

        await Eventually.Until(() => Inputs.OfType<HandoffInput.MouseUp>().Any(), "the tap to click");
        (await page.EvaluateAsync<string>("() => document.activeElement?.id ?? \"\"")).ShouldNotBe("keys");
    }

    // A phone that leaves for its authenticator app drops the view's socket. The view used to end the
    // login there; it now finds the login still open and connects again.
    [Fact]
    public async Task ADroppedView_ConnectsAgain_WhileTheLoginIsOpen()
    {
        var page = await PhoneAsync();
        var sockets = new ConcurrentQueue<IWebSocketRoute>();
        await page.Context.RouteWebSocketAsync("**/handoff/live", ws =>
        {
            ws.ConnectToServer();
            sockets.Enqueue(ws);
        });
        await page.GotoAsync(await LinkAsync(FromChatClient(Fran)));
        await Eventually.Until(() => Inputs.OfType<HandoffInput.Resize>().Count() == 1, "the view to connect");

        await sockets.Single().CloseAsync();

        await Eventually.Until(() => Inputs.OfType<HandoffInput.Resize>().Count() == 2, "the view to connect again");
        Inputs.OfType<HandoffInput.Resize>().Distinct().ShouldHaveSingleItem("connecting again says the same size");
        await Assertions.Expect(page.Locator("#done")).ToBeEnabledAsync();
    }

    // A page opened again — reloaded, or brought back after the phone let it go — finds the login its
    // browser claimed rather than calling its spent link the end of it.
    [Fact]
    public async Task APageOpenedAgain_TakesUpItsLogin()
    {
        var page = await OpenAsync(FromChatClient(Fran));

        await page.ReloadAsync();

        await Assertions.Expect(page.Locator("#title")).ToHaveTextAsync("Log in to shop.test");
        await Assertions.Expect(page.Locator("#done")).ToBeEnabledAsync();
    }

    // The link was opened from the chat, in the installed app's own window: once logged in, the page
    // goes back there by itself, with a button for it besides.
    [Fact]
    public async Task Done_FromTheChat_GoesBackToTheChat()
    {
        var page = await PhoneAsync();
        var chat = $"{_server.PagesAddress}/browse/{Fran.TenantId}/chat-stand-in";
        await page.GotoAsync(chat);
        await page.GotoAsync(await LinkAsync(FromChatClient(Fran)));
        await Assertions.Expect(page.Locator("#done")).ToBeEnabledAsync();

        await page.Locator("#done").ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Back to the chat" })).ToBeVisibleAsync();
        await page.WaitForURLAsync(chat);
    }

    [Fact]
    public async Task Done_FromTelegram_SaysToGoBackThere()
    {
        var page = await OpenAsync(FromTelegram(Fran));

        await page.Locator("#done").ClickAsync();

        await Assertions.Expect(page.GetByText("You can go back to Telegram now.")).ToBeVisibleAsync();
    }

    // Nothing in the agent's browser can answer a passkey, and a site that asks for one is refused at
    // once — which a person sees only as the site saying something went wrong, so the view says why.
    [Fact]
    public async Task TheView_SaysPasskeysCannotBeUsedHere()
    {
        var page = await OpenAsync(FromChatClient(Fran));

        await Assertions.Expect(page.GetByText("Passkeys can't be used here")).ToBeVisibleAsync();
    }

    private IReadOnlyList<HandoffInput> Inputs => [.. _browser.Inputs];

    private async Task<string> LinkAsync(ConversationContext from)
    {
        var envelope = Envelope(await _server.CallAsync("web_login",
            new Dictionary<string, object?> { ["url"] = "https://shop.test/login" }, from));
        var link = envelope["link"]?.GetValue<string>() ?? throw new InvalidOperationException(envelope.ToJsonString());
        return _server.PagesAddress + new Uri(link).AbsolutePath;
    }

    private async Task<IPage> PhoneAsync()
    {
        var context = await _chromium.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            DeviceScaleFactor = 2,
            IsMobile = true,
            HasTouch = true
        });
        return await context.NewPageAsync();
    }

    // Open, connected, and showing the site.
    private async Task<IPage> OpenAsync(ConversationContext from)
    {
        var page = await PhoneAsync();
        await page.GotoAsync(await LinkAsync(from));
        await Assertions.Expect(page.Locator("#status")).ToBeHiddenAsync();
        await Eventually.Until(() => Inputs.OfType<HandoffInput.Resize>().Any(), "the view to say its size");
        return page;
    }

    // A login page's picture, as a browser's screencast would send one: phone-shaped, so it fills the view.
    private async Task<byte[]> PictureAsync()
    {
        var page = await _chromium.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 360, Height = 640 } });
        await page.SetContentAsync("<body style='margin:0;background:#fff'><h1>Shop</h1></body>");
        var jpeg = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg });
        await page.CloseAsync();
        return jpeg;
    }

    // A finger moved across the screen as a phone reports it, which a synthetic pointer event is not.
    private static async Task DragAsync(IPage page, double x, double fromY, double toY)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        Dictionary<string, object> Touch(string type, double y) => new()
        {
            ["type"] = type,
            ["touchPoints"] = type == "touchEnd" ? Array.Empty<object>() : new object[] { new { x, y } }
        };
        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchStart", fromY));
        foreach (var step in Enumerable.Range(1, 10))
        {
            await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchMove", fromY + (toY - fromY) * step / 10));
        }

        await cdp.SendAsync("Input.dispatchTouchEvent", Touch("touchEnd", toY));
    }
}