using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

// Hermetic guards for "the agent can drive real-jQuery widgets through Camoufox".
//
// These replace flaky live-navitime tests (and hand-rolled jQuery shims). They inject the REAL
// jQuery library into a controlled page and assert our web_action Type/Click drive genuine jQuery
// event handlers — the same compatibility surface real sites expose, but deterministic and offline.
//
// Why this is a real guard, not a tautology: the production code used to dispatch jQuery/native
// events explicitly to work around Camoufox's anti-detect synthetic events. That code was removed
// after verifying (against live navitime) that current Camoufox fires trusted jQuery focus/keyup/
// input from plain Playwright actions. With that compensation gone, these tests pass only because
// Camoufox does it natively — so if a future Camoufox regresses, they fail and tell us the
// workaround needs to come back.
[Collection(PlaywrightCollections.SharedBrowser)]
public class BrowserJQueryWidgetCompatTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ClearContextStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // A readonly input whose dropdown opens ONLY via a real jQuery focus handler — the
    // bootstrap-datepicker pattern. Real jQuery binds focus through addEventListener, so a genuine
    // click-driven focus must reach it.
    private const string FocusWidgetJs = """
        var $ = window.jQuery;
        document.body.innerHTML =
            '<input id="d" type="text" readonly value="2026/04/06" style="cursor:pointer;width:200px">' +
            '<div id="dd" role="listbox" style="display:none">' +
            '  <button role="option" data-v="2026/04/07">April 7</button>' +
            '  <button role="option" data-v="2026/04/08">April 8</button></div>';
        $('#d').on('focus', function(){ document.getElementById('dd').style.display = 'block'; });
        $('#dd button').on('click', function(){
            document.getElementById('d').value = this.getAttribute('data-v');
            document.getElementById('dd').style.display = 'none';
        });
        """;

    // A jQuery autocomplete bound to input/keyup that filters a local list — the navitime pattern.
    private const string AutocompleteJs = """
        var $ = window.jQuery;
        document.body.innerHTML =
            '<input id="q" type="text" autocomplete="off" style="width:200px">' +
            '<ul id="ac" role="listbox" style="display:none"></ul>' +
            '<input type="hidden" id="code">';
        var DATA = [{name:'Odawara', code:'ODW'}, {name:'Tokyo', code:'TYO'}, {name:'Osaka', code:'OSA'}];
        $('#q').on('input keyup', function(){
            var v = this.value.toLowerCase();
            var ul = document.getElementById('ac');
            ul.innerHTML = '';
            var matches = v ? DATA.filter(function(d){ return d.name.toLowerCase().indexOf(v) === 0; }) : [];
            ul.style.display = matches.length ? 'block' : 'none';
            matches.forEach(function(d){
                var li = document.createElement('li');
                li.setAttribute('role', 'option');
                li.textContent = d.name;
                $(li).on('click', function(){
                    document.getElementById('code').value = d.code;
                    document.getElementById('q').value = d.name;
                    ul.style.display = 'none';
                });
                ul.appendChild(li);
            });
        });
        """;

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Click_OpensRealJQueryFocusWidget()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");
        var sessionId = $"jq-focus-{Guid.NewGuid():N}";
        try
        {
            await PrepareWidgetAsync(sessionId, FocusWidgetJs);
            var inputRef = await FindRefAsync(sessionId, l => l.Contains("textbox"));

            var click = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, inputRef, WebActionType.Click));

            click.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine(click.Snapshot);
            // The dropdown options become visible only if our click fired the real jQuery focus handler.
            click.Snapshot.ShouldNotBeNull();
            click.Snapshot.ShouldContain("option");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ClickOption_ClosesRealJQueryDropdown()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");
        var sessionId = $"jq-close-{Guid.NewGuid():N}";
        try
        {
            await PrepareWidgetAsync(sessionId, FocusWidgetJs);
            var inputRef = await FindRefAsync(sessionId, l => l.Contains("textbox"));

            var open = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, inputRef, WebActionType.Click));
            open.Status.ShouldBe(WebActionStatus.Success);
            var optionRef = Regex.Match(
                open.Snapshot!.Split('\n').First(l => l.Contains("option") && l.Contains("ref=")),
                @"ref=(e-\d+)").Groups[1].Value;

            var select = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, optionRef, WebActionType.Click));

            select.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine(select.Snapshot);
            var diff = select.Snapshot!.Split('\n');
            diff.ShouldContain(l => l.StartsWith("- ") && l.Contains("option"),
                "Selecting an option should close the dropdown (removed option lines in the diff).");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task Type_DrivesRealJQueryAutocomplete_AndSelectionSetsHiddenInput()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");
        var sessionId = $"jq-ac-{Guid.NewGuid():N}";
        try
        {
            await PrepareWidgetAsync(sessionId, AutocompleteJs);
            var inputRef = await FindRefAsync(sessionId, l => l.Contains("textbox"));

            var type = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, inputRef, WebActionType.Type, Value: "Oda"));
            type.Status.ShouldBe(WebActionStatus.Success);

            // The suggestion list populates only if our typing fired the real jQuery input/keyup handler.
            var snapshot = await fixture.WaitForSnapshotAsync(
                sessionId,
                s => Regex.IsMatch(s, @"option ""Odawara"" \[ref=e-\d+\]"),
                "Odawara autocomplete suggestion");
            var optionRef = Regex.Match(snapshot, @"option ""Odawara"" \[ref=(e-\d+)\]").Groups[1].Value;

            var select = await fixture.Browser.ActionAsync(
                new WebActionRequest(sessionId, optionRef, WebActionType.Click));
            select.Status.ShouldBe(WebActionStatus.Success);

            // Selecting wired the hidden code via the jQuery click handler.
            await fixture.Browser.EvaluateOnSessionAsync(sessionId, """
                () => { if (document.getElementById('code').value !== 'ODW') throw new Error('hidden code not set'); }
                """);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // Navigates to a stable blank anchor (the fixture already warms up on example.com), injects the
    // real jQuery library, then builds the widget. NavigateAsync only accepts http/https, so a
    // neutral anchor page is the canvas we inject onto.
    private async Task PrepareWidgetAsync(string sessionId, string widgetJs)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com"));
        nav.Status.ShouldBe(BrowseStatus.Success);

        var jquery = await File.ReadAllTextAsync(JQueryAssetPath());
        var jqueryLiteral = JsonSerializer.Serialize(jquery);
        await fixture.Browser.EvaluateOnSessionAsync(sessionId,
            $"() => {{ eval({jqueryLiteral}); {widgetJs} }}");
    }

    private async Task<string> FindRefAsync(string sessionId, Func<string, bool> lineMatch)
    {
        var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
        snapshot.ErrorMessage.ShouldBeNull();
        var line = snapshot.Snapshot!.Split('\n').First(l => lineMatch(l) && l.Contains("ref="));
        return Regex.Match(line, @"ref=(e-\d+)").Groups[1].Value;
    }

    private static string JQueryAssetPath()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ziggurat.sln")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "Tests/Integration/Assets/jquery-3.7.1.min.js");
    }
}