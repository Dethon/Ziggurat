using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;

namespace Tests.Eval.Fixtures;

// The served web, proven against the real search client and the real browser. Nothing here needs a
// model: what is checked is that a scenario running against this site runs against the same
// surface a deployment does — Brave's own response shape through Brave's own client, and a page
// read and acted on through the browser the web tools use.
public class EvalWebTests : IAsyncLifetime
{
    private EvalWeb _web = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private PlaywrightWebBrowser _browsing = null!;

    public async Task InitializeAsync()
    {
        _web = await EvalWeb.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Proxy = new Proxy { Server = "http://127.0.0.1:1", Bypass = "127.0.0.1,localhost" }
        });
        _browsing = new PlaywrightWebBrowser(browserFactory: () => Task.FromResult(_browser));
    }

    public async Task DisposeAsync()
    {
        await _browsing.DisposeAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
        await _web.DisposeAsync();
    }

    [Fact]
    public async Task SearchingForTheRecipe_ReturnsItsUrlAndNotTheAnswer()
    {
        var client = new BraveSearchClient(
            new HttpClient(_web.Search) { BaseAddress = new Uri("http://brave.eval/res/v1/") }, "eval");

        var result = await client.SearchAsync(new WebSearchQuery("gazpacho de Almudena", 10));

        var first = result.Results.ShouldHaveSingleItem();
        first.Url.ShouldBe(_web.RecipeUrl);
        // The snippet says nothing about the resting time. A scenario that answered from it would
        // have nothing to answer with, which is what makes reading the page the only way through.
        first.Snippet.ShouldNotContain(EvalWeb.RestingMinutes);
    }

    [Fact]
    public async Task TheMuseumsSnippetAndItsPage_Disagree()
    {
        var client = new BraveSearchClient(
            new HttpClient(_web.Search) { BaseAddress = new Uri("http://brave.eval/res/v1/") }, "eval");

        var result = await client.SearchAsync(new WebSearchQuery("horario del museo", 10));

        result.Results.ShouldHaveSingleItem().Snippet.ShouldContain(EvalWeb.StaleOpeningTime);
        var page = await _browsing.NavigateAsync(new BrowseRequest("proof-museum", _web.MuseumUrl));
        page.Content.ShouldContain(EvalWeb.OpeningTime);
    }

    [Fact]
    public async Task APageNobodyHereServes_CannotBeLoadedAtAll()
    {
        // The family's hard boundary, held by the browser rather than by a rule somebody has to
        // remember: everything but loopback is proxied to a port with nothing behind it.
        var result = await _browsing.NavigateAsync(new BrowseRequest("proof-offline", "https://example.com"));

        result.Status.ShouldNotBe(BrowseStatus.Success);
    }

    [Fact]
    public async Task ReadingTheRecipe_ReturnsTheFactThatIsOnlyOnThePage()
    {
        var result = await _browsing.NavigateAsync(new BrowseRequest("proof-recipe", _web.RecipeUrl));

        result.Status.ShouldBe(BrowseStatus.Success);
        result.Content.ShouldContain($"{EvalWeb.RestingMinutes} minutos");
    }

    [Fact]
    public async Task FillingTheFormAndSubmittingIt_BooksThePlaceAndAnswersWithTheCode()
    {
        // The whole action path, through the same browser the tools use: the refs come from a
        // snapshot, the values go in by ref, and the submit navigates. If this cannot be done here
        // then a scenario failing it says nothing about the agent.
        const string session = "proof-booking";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.BookingUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        await _browsing.ActionAsync(new WebActionRequest(
            session, Ref(snapshot, "textbox"), WebActionType.Fill, "Fran"));
        await _browsing.ActionAsync(new WebActionRequest(
            session, Ref(snapshot, "combobox"), WebActionType.Select, "Sábado 12:00"));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, Ref(snapshot, "button"), WebActionType.Click, WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        var booking = _web.Bookings.ShouldHaveSingleItem();
        booking.Name.ShouldBe("Fran");
        booking.Slot.ShouldBe("Sábado 12:00");

        // The click lands on the confirmation's own url, which is what leaves the code readable
        // by anything that loads the page afterwards.
        submitted.Url.ShouldStartWith(_web.ConfirmationUrl);
        var confirmation = await _browsing.GetCurrentPageAsync(session);
        confirmation.Content.ShouldContain(EvalWeb.BookingCode);
    }

    // The accessibility tree names each element by role and hands it a ref; the scenarios read the
    // same tree the model reads, so a change in how refs are rendered fails here first.
    private static string Ref(SnapshotResult snapshot, string role) =>
        snapshot.Snapshot!
            .Split('\n')
            .Where(line => line.Contains(role, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf("[ref=", StringComparison.Ordinal) + 5)..])
            .Select(rest => rest[..rest.IndexOf(']')])
            .First();
}