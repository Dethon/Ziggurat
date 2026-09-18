using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Tools.Web;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;
using Tests.Unit.Judgments;

namespace Tests.Integration.Clients;

// The third step of the dismisser through a real browse, against a scripted judge: a wall no
// selector and no word list closes is closed by what its buttons say, by index into the list the
// names were read from, and the browse envelope names the click as `judgment(<index>)`. The
// browser is the test's own so it can carry the judge and the recording publisher.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class ModalJudgmentDismissalTests(IsolatedSessionBrowserFixture fixture) : IAsyncLifetime
{
    private readonly RecordingMetricsPublisher _published = new();
    private readonly List<PlaywrightWebBrowser> _browsers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var browser in _browsers)
        {
            await browser.DisposeAsync();
        }
    }

    // Two controls, neither in any word list, the reject second: the pick has to be the index and
    // not the first thing on the wall.
    private const string CookieWall =
        "<div id='cmp' class='cookie-consent' " +
        "style='position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999'>" +
        "Usamos cookies. " +
        "<button class='cmp-secondary' onclick=\"document.title='SETTINGS'\">Configurar</button>" +
        "<button class='cmp-primary' onclick=\"document.getElementById('cmp').style.display='none'\">Rechazar todo</button>" +
        "</div><p>Main content</p>";

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_AWallOnlyAJudgmentCloses_ClicksThePickByIndex()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");
        var judge = Choices((ModalJudge.RejectQuestionId, "1", 0.9), (ModalJudge.AcceptQuestionId, ModalJudge.NoneChoice, 0.8));
        var browser = await BrowserAsync(judge);
        var url = await ServeAsync(browser, CookieWall);
        var sessionId = Guid.NewGuid().ToString();

        var result = await browser.NavigateAsync(new BrowseRequest(sessionId, url));

        var dismissed = result.DismissedModals.ShouldNotBeNull().ShouldHaveSingleItem();
        dismissed.Type.ShouldBe(ModalType.CookieConsent);
        dismissed.Selector.ShouldBe("judgment(1)");
        dismissed.ButtonText.ShouldBe("Rechazar todo");
        (await browser.EvaluateOnSessionAsync<bool>(sessionId,
            "() => document.getElementById('cmp').style.display === 'none' && document.title !== 'SETTINGS'")).ShouldBeTrue();

        var evt = _published.Published.OfType<ModalDismissalEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.Judgment);
        evt.Selector.ShouldBe("judgment(1)");
        evt.Confidence.ShouldBe(0.9);
        evt.DurationMs.ShouldNotBeNull();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_TheJudgeSaysNone_LeavesTheWallAndCountsTheMiss()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");
        var judge = Choices((ModalJudge.RejectQuestionId, ModalJudge.NoneChoice, 0.95), (ModalJudge.AcceptQuestionId, ModalJudge.NoneChoice, 0.95));
        var browser = await BrowserAsync(judge);
        var url = await ServeAsync(browser, CookieWall);
        var sessionId = Guid.NewGuid().ToString();

        var result = await browser.NavigateAsync(new BrowseRequest(sessionId, url));

        result.DismissedModals.ShouldBeEmpty();
        (await browser.EvaluateOnSessionAsync<bool>(sessionId,
            "() => document.getElementById('cmp').style.display !== 'none' && document.title !== 'SETTINGS'")).ShouldBeTrue();
        var evt = _published.Published.OfType<ModalDismissalEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(ModalDismissalOutcomes.LeftStanding);
        evt.Confidence.ShouldBe(0.95);
    }

    // What the judge is shown: the wall's own controls by name and role, in document order, and
    // never a link that would carry the browser off the page — nor the page's url or its words.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_TheJudgeIsShownTheWallsControls_AndNothingOfThePage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");
        var judge = Choices((ModalJudge.RejectQuestionId, ModalJudge.NoneChoice, 0.9), (ModalJudge.AcceptQuestionId, ModalJudge.NoneChoice, 0.9));
        var browser = await BrowserAsync(judge);
        var url = await ServeAsync(browser,
            "<button id='page-own'>Iniciar sesión</button>" +
            "<div id='cmp' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#ddd;z-index:9999'>" +
            "Usamos cookies para personalizar tu experiencia. " +
            "<a href='https://elsewhere.test/privacy'>Más información</a>" +
            "<a href='#' onclick=\"return false\">Configurar</a>" +
            "<button aria-label='Rechazar todo'><svg></svg></button>" +
            "<button style='display:none'>Oculto</button>" +
            "</div><p>Main content</p>");

        await browser.NavigateAsync(new BrowseRequest(Guid.NewGuid().ToString(), url));

        var request = judge.Requests.ShouldHaveSingleItem();
        request.State.Select(p => p.Key).ShouldBe(["overlay_kind", "controls"]);
        request.State["overlay_kind"]!.GetValue<string>().ShouldBe(ModalKinds.Cookie);
        request.State["controls"]!.AsArray()
            .Select(c => (c!["role"]!.GetValue<string>(), c["name"]!.GetValue<string>()))
            .ShouldBe([("link", "Configurar"), ("button", "Rechazar todo")]);
        request.State.ToJsonString().ShouldNotContain(url);
        request.State.ToJsonString().ShouldNotContain("personalizar");
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Navigate_AJudgeWithNoKey_LeavesThePageExactlyAsToday()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");
        var browser = await BrowserAsync(StubJudge.Absent(AbsenceReason.Unconfigured));
        var url = await ServeAsync(browser, CookieWall);
        var sessionId = Guid.NewGuid().ToString();

        var result = await browser.NavigateAsync(new BrowseRequest(sessionId, url));

        result.DismissedModals.ShouldBeEmpty();
        _published.Published.OfType<ModalDismissalEvent>().ShouldHaveSingleItem().Outcome.ShouldBe(ModalDismissalOutcomes.LeftStanding);
    }

    private async Task<PlaywrightWebBrowser> BrowserAsync(IJudge judge)
    {
        var dismisser = new ModalDismisser(new ModalJudge(judge, new ModalJudgmentSettings(), TimeProvider.System));
        var browser = new PlaywrightWebBrowser(
            wsEndpoint: fixture.WsEndpoint, modalDismisser: dismisser, metricsPublisher: _published);
        _browsers.Add(browser);
        await browser.EnsureInitializedAsync();
        return browser;
    }

    private static async Task<string> ServeAsync(PlaywrightWebBrowser browser, string body)
    {
        var url = $"https://modal-judgment-{Guid.NewGuid():N}.test/";
        await browser.RouteOnContextAsync(url, route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = $"<!DOCTYPE html><html><head><title>wall</title></head><body>{body}</body></html>"
        }));
        return url;
    }

    private static StubJudge Choices(params (string Id, string Choice, double Confidence)[] answers) =>
        new(new JudgmentOutcome.Answered(new Judgment(
            "jev-test",
            answers.ToDictionary(
                a => a.Id,
                a => (JudgmentAnswer)new ChoiceAnswer(a.Choice, a.Confidence, new Dictionary<string, double> { [a.Choice] = a.Confidence }),
                StringComparer.Ordinal),
            new JudgmentUsage(100, 0))));
}