using Domain.Contracts;
using Domain.DTOs;
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
        _browser = await EvalWeb.LaunchBrowserAsync(_playwright);
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
        var result = await _web.SearchClient().SearchAsync(new WebSearchQuery("gazpacho de Almudena", 10));

        var first = result.Results.ShouldHaveSingleItem();
        first.Url.ShouldBe(_web.RecipeUrl);
        // The snippet says nothing about the resting time. A scenario that answered from it would
        // have nothing to answer with, which is what makes reading the page the only way through.
        first.Snippet.ShouldNotContain(EvalWeb.RestingMinutes);
    }

    [Fact]
    public async Task TheMuseumsSnippetAndItsPage_Disagree()
    {
        var result = await _web.SearchClient().SearchAsync(new WebSearchQuery("horario del museo", 10));

        result.Results.ShouldHaveSingleItem().Snippet.ShouldContain(EvalWeb.StaleOpeningTime);
        var page = await _browsing.NavigateAsync(new BrowseRequest("proof-museum", _web.MuseumUrl));
        page.Content.ShouldNotBeNull().ShouldContain(EvalWeb.OpeningTime);
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
        result.Content.ShouldNotBeNull().ShouldContain($"{EvalWeb.RestingMinutes} minutos");
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
            session, Ref(snapshot, "combobox"), WebActionType.Select, EvalWeb.SaturdaySlot));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, Ref(snapshot, "button"), WebActionType.Click, WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        var booking = _web.Bookings.ShouldHaveSingleItem();
        booking.Name.ShouldBe("Fran");
        booking.Slot.ShouldBe(EvalWeb.SaturdaySlot);

        // The click lands on the confirmation's own url, which is what leaves the code readable
        // by anything that loads the page afterwards.
        submitted.Url.ShouldStartWith(_web.ConfirmationUrl);
        var confirmation = await _browsing.GetCurrentPageAsync(session);
        // The Saturday code rather than any code: booking the wrong turn produces the other one.
        confirmation.Content.ShouldNotBeNull().ShouldContain(EvalWeb.SaturdayCode);
        confirmation.Content.ShouldNotContain(EvalWeb.SundayCode);
    }

    [Fact]
    public async Task TheChronicle_OverflowsOneDefaultBrowse_AndItsTotalLivesOnlyAtTheEnd()
    {
        // The page the partial-content rule needs: a first fetch at the default length truncates
        // and does not carry the number, so a reply with the number in it paid for the tail.
        var first = await _browsing.NavigateAsync(new BrowseRequest("proof-chronicle", _web.ChronicleUrl));

        first.Status.ShouldBe(BrowseStatus.Success);
        first.Truncated.ShouldBeTrue();
        first.Content.ShouldNotBeNull().ShouldNotContain(EvalWeb.RaffleTotal);

        var rest = await _browsing.NavigateAsync(new BrowseRequest(
            "proof-chronicle", _web.ChronicleUrl, Offset: first.Content!.Length, MaxLength: 100_000));
        rest.Truncated.ShouldBeFalse();
        rest.Content.ShouldNotBeNull().ShouldContain(EvalWeb.RaffleTotal);
    }

    [Fact]
    public async Task TypingInTheActivityField_OpensSuggestions_AndPickingOneSignsUp()
    {
        // The reactive field the type-vs-fill rule needs, driven end to end: the suggestions only
        // exist after keystrokes, and the signup only lands when one of them was picked.
        const string session = "proof-signup";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.SignupUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        var typed = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Actividad"), WebActionType.Type, "astro"));
        typed.Status.ShouldBe(WebActionStatus.Success);

        var suggestions = await _browsing.SnapshotAsync(new SnapshotRequest(session));
        suggestions.Snapshot.ShouldNotBeNull().ShouldContain("Astronomía en la azotea");

        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(suggestions, "button", "Astronomía en la azotea"), WebActionType.Click));

        // Every action re-stamps refs in document order, and the pick removed elements — so the
        // numbers shifted, and the flow re-snapshots the way the tool tells the model to when the
        // diff does not show what it needs.
        var settled = await _browsing.SnapshotAsync(new SnapshotRequest(session));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(settled, "textbox", "Nombre"), WebActionType.Fill, "Fran"));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(settled, "button", "Apuntarse"), WebActionType.Click, WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        var signup = _web.Signups.ShouldHaveSingleItem();
        signup.Name.ShouldBe("Fran");
        signup.ActivityId.ShouldBe("astro");
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldContain(EvalWeb.SignupCode);
    }

    [Fact]
    public async Task FillingTheActivityField_OpensNoSuggestionsAtAll()
    {
        // The trap has to bite: Playwright's fill dispatches an input event, so a page listening
        // on input opens its suggestions for a filled value and type-vs-fill is indistinguishable
        // — the first armed run proved it. The list is driven by key events, which only real
        // keystrokes produce, so a filled field stays suggestion-less and typing is the only way
        // to a code.
        const string session = "proof-fill-inert";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.SignupUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Actividad"), WebActionType.Fill, "astro"));

        (await _browsing.SnapshotAsync(new SnapshotRequest(session))).Snapshot.ShouldNotBeNull()
            .ShouldNotContain("Astronomía en la azotea");
    }

    [Fact]
    public async Task SubmittingWithoutPickingASuggestion_DoesNotSignUp()
    {
        // Picking from the list is load-bearing: the hidden id is only set by the suggestion's own
        // click handler, so a form filled by value alone bounces and prints no code.
        const string session = "proof-signup-unpicked";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.SignupUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Actividad"), WebActionType.Fill, "Astronomía en la azotea"));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Nombre"), WebActionType.Fill, "Fran"));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "button", "Apuntarse"), WebActionType.Click, WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        _web.Signups.ShouldBeEmpty();
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldNotContain(EvalWeb.SignupCode);
    }

    [Fact]
    public async Task TheArchive_HidesItsEditionUrls_AndBackReturnsToIt()
    {
        // The back rule is only forced if the edition urls leak nowhere: a content read or a
        // snapshot that carried them would let a model browse forward and never return.
        const string session = "proof-archive";
        var archive = await _browsing.NavigateAsync(new BrowseRequest(session, _web.ArchiveUrl));
        archive.Content.ShouldNotBeNull().ShouldNotContain("ed-3f7");
        archive.Content.ShouldNotContain("ed-b12");

        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));
        snapshot.Snapshot.ShouldNotBeNull().ShouldNotContain("ed-3f7");

        var opened = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "button", "La rifa de 2024"), WebActionType.Click,
            WaitForNavigation: true));
        opened.Status.ShouldBe(WebActionStatus.Success);
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldContain(EvalWeb.Raffle2024Total);

        var back = await _browsing.ActionAsync(new WebActionRequest(
            session, Action: WebActionType.Back));
        back.Status.ShouldBe(WebActionStatus.Success);

        var again = await _browsing.SnapshotAsync(new SnapshotRequest(session));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(again, "button", "La rifa de 2025"), WebActionType.Click,
            WaitForNavigation: true));
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldContain(EvalWeb.Raffle2025Total);
    }

    [Fact]
    public async Task TheMaterialsForm_TakesFourFieldsAndAnswersWithTheCode()
    {
        // The chaining page: four fields and a submit from one snapshot's refs — the form is
        // static, so nothing re-stamps between actions and no second snapshot is ever needed.
        const string session = "proof-materials";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.MaterialsUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Nombre"), WebActionType.Fill, "Fran"));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Teléfono"), WebActionType.Fill, "600111222"));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Correo"), WebActionType.Fill, "fran@example.com"));
        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "combobox", "Taller"), WebActionType.Select,
            EvalWeb.CeramicsWorkshop));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "button", "Enviar"), WebActionType.Click,
            WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        var signup = _web.Materials.ShouldHaveSingleItem();
        signup.Name.ShouldBe("Fran");
        signup.Workshop.ShouldBe(EvalWeb.CeramicsWorkshop);
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldContain(EvalWeb.MaterialsCode);
    }

    [Fact]
    public async Task SubmittingTheMaterialsFormIncomplete_PrintsNoCode()
    {
        // Every field is load-bearing: the code is only behind a form filled whole, so a flow
        // that skipped a field cannot pass by submitting anyway.
        const string session = "proof-materials-short";
        await _browsing.NavigateAsync(new BrowseRequest(session, _web.MaterialsUrl));
        var snapshot = await _browsing.SnapshotAsync(new SnapshotRequest(session));

        await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "textbox", "Nombre"), WebActionType.Fill, "Fran"));
        var submitted = await _browsing.ActionAsync(new WebActionRequest(
            session, RefBy(snapshot, "button", "Enviar"), WebActionType.Click,
            WaitForNavigation: true));

        submitted.Status.ShouldBe(WebActionStatus.Success);
        _web.Materials.ShouldBeEmpty();
        (await _browsing.GetCurrentPageAsync(session)).Content.ShouldNotBeNull()
            .ShouldNotContain(EvalWeb.MaterialsCode);
    }

    // A ref by role AND accessible name: the tree names a listitem after the button inside it, so
    // a name-only lookup picks the wrapper and a click on it never reaches the button's handler.
    private static string RefBy(SnapshotResult snapshot, string role, string name) =>
        snapshot.Snapshot!
            .Split('\n')
            .Where(line => line.Contains($"{role} \"{name}\"", StringComparison.OrdinalIgnoreCase)
                           && line.Contains("[ref=", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("[ref=", StringComparison.Ordinal) + 5)..])
            .Select(rest => rest[..rest.IndexOf(']')])
            .First();

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