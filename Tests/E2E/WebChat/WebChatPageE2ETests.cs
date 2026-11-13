using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// The chat page itself, signed in: the welcome at rest, the connection status, the composer's controls
// and spacing, a slug that is no space, and a page that is its account. None reads the household's name
// or agents, so they run apart from the Chat collection, which keeps the cases that do beside the
// settings page that changes them.
[Collection(WebChatE2ECollections.Page)]
[Trait("Category", "E2E")]
public class WebChatPageE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task IdleWelcomeScreen_HasNoPerpetualAnimations()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        // The welcome/empty-state screen is the idle foreground state (no conversation selected).
        // Any CSS animation that loops forever here keeps the browser compositor awake every frame,
        // pinning GPU usage even while the user is idle. The empty-state must declare no such animation.
        //
        // Scope strictly to the .empty-state subtree. Other infinite animations elsewhere on the page
        // are transient work-indicators, not idle decorations — e.g. the sidebar topic-streaming
        // indicator (.topic-streaming-indicator, 3 pulsing dots) which a stream resumed on load
        // (StreamResumeService) can render against the shared E2E server state polluted by sibling
        // tests. Those loop only while a stream is active and are not part of the idle foreground this
        // guard covers; a document-wide query made this assertion flake (intermittently saw 3).
        var emptyState = page.Locator(".empty-state");
        await emptyState.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        const string countPerpetualInEmptyState = @"() => {
            const root = document.querySelector('.empty-state');
            return root
                ? document.getAnimations().filter(a => a.playState === 'running'
                    && a.effect
                    && a.effect.getComputedTiming().iterations === Infinity
                    && a.effect.target
                    && root.contains(a.effect.target)).length
                : 0;
        }";

        var perpetualAnimations = await page.EvaluateAsync<int>(countPerpetualInEmptyState);

        // Self-diagnosing failure: report which animations loop and on which element. Only queried on
        // failure so a green run stays cheap.
        var offenders = perpetualAnimations == 0
            ? "[]"
            : await page.EvaluateAsync<string>(@"() => {
                const root = document.querySelector('.empty-state');
                const hits = root ? document.getAnimations().filter(a => a.playState === 'running'
                    && a.effect
                    && a.effect.getComputedTiming().iterations === Infinity
                    && a.effect.target
                    && root.contains(a.effect.target)) : [];
                return JSON.stringify(hits.map(a => ({
                    animationName: a.animationName,
                    tag: a.effect.target.tagName ? a.effect.target.tagName.toLowerCase() : '(pseudo)',
                    cls: (a.effect.target.getAttribute && a.effect.target.getAttribute('class')) || '(none)',
                    pseudo: a.effect.pseudoElement || null
                })));
            }");

        perpetualAnimations.ShouldBe(
            0,
            $"Idle .empty-state must declare no perpetually-looping animations, but found: {offenders}");
    }

    // A slug that is none of the household's spaces is nobody's space to show: the page opens the
    // household's first space instead, under the deployment's own name.
    [SkippableFact]
    public async Task ASlugThatIsNoSpaceOfTheHousehold_OpensItsFirstSpace()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.DefaultSpaceUrl + "nowhere");
        await SelectAgentAsync(page);

        await Assertions.Expect(page).ToHaveTitleAsync("Herfluffness' Assistants");
    }

    [SkippableFact]
    public async Task ConnectionStatus_ShowsConnected()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        // Allow up to 30 seconds for the hub to become reachable and the Blazor
        // client to complete the handshake.
        var connectedDot = page.Locator(".connection-status .status-dot.connected");
        await connectedDot.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await connectedDot.IsVisibleAsync()).ShouldBeTrue();
    }

    // The field sits between two icons and the eye reads it as centred between them, so the two
    // gaps have to be one gap. They were written in two places -- the row's own gap on one side of
    // the field, the wrapper holding attach and field on the other -- and drifted apart, leaving
    // the field closer to the clip than to the control opposite it.
    [SkippableFact]
    public async Task TheComposerField_SitsTheSameDistanceFromTheControlOnEitherSide()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        var field = page.Locator("textarea.chat-input");
        var attach = page.Locator("label.attach-button");

        // The microphone holds the right-hand place until there is something to send.
        await AssertComposerGapsMatchAsync(attach, field, page.Locator("[data-testid=dictation-mic]"));

        await field.FillAsync("something to send");
        await AssertComposerGapsMatchAsync(attach, field, page.Locator("[data-testid=composer-send]"));
    }

    // Attach, send and cancel are drawn for the same reason the microphone is: a text label beside
    // a row of icons reads as a different kind of control, and the platform's own "+" is neither
    // the weight nor the colour of the shapes next to it.
    [SkippableFact]
    public async Task TheComposerControls_AreDrawnAsIconsRatherThanWords()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        var attach = page.Locator("label.attach-button");
        await Assertions.Expect(attach.Locator("svg")).ToBeVisibleAsync();
        (await attach.InnerTextAsync()).Trim().ShouldBeEmpty();

        // Send only takes the microphone's place once there is something to send.
        await page.Locator("textarea.chat-input").FillAsync("something to send");

        var send = page.Locator("[data-testid=composer-send]");
        await Assertions.Expect(send.Locator("svg")).ToBeVisibleAsync();
        (await send.InnerTextAsync()).Trim().ShouldBeEmpty();

        // Every one of them says what it is to a screen reader, which the words used to do.
        (await attach.GetAttributeAsync("title")).ShouldNotBeNullOrEmpty();
        (await send.GetAttributeAsync("aria-label")).ShouldNotBeNullOrEmpty();
    }

    // Nobody is picked any more: a signed-in page is its account, named in the header from the
    // accounts service, and the configuration the page reads lists nobody.
    [SkippableFact]
    public async Task ASignedInPage_IsItsAccountWithNoUserToPick()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var user = fixture.NextUserIndex();
        var page = await fixture.CreateSignedInPageAsync(user);
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);

        await Assertions.Expect(page.Locator(".signed-in-person"))
            .ToHaveAttributeAsync("title", WebChatE2EFixture.NameOf(user));
        (await page.Locator(".user-dropdown-menu, .avatar-button").CountAsync()).ShouldBe(0);

        var config = await page.APIRequest.GetAsync(new Uri(new Uri(fixture.WebChatUrl), "/api/config").ToString());
        config.Status.ShouldBe(200);
        var configured = JsonNode.Parse(await config.TextAsync())!.AsObject();
        configured.Select(p => p.Key).ShouldNotContain(key => key.Equals("users", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertComposerGapsMatchAsync(ILocator left, ILocator field, ILocator right)
    {
        // BoundingBoxAsync does not wait: it answers null for a control that is not on screen yet,
        // and the assertion then blames the gap rather than the render. The right-hand control is
        // the one that moves — the microphone holds that place until there is something to send and
        // the send button takes it over — so a measurement taken mid-swap reads one box from before
        // it and one from after.
        await Assertions.Expect(left).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(field).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(right).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var leftBox = await left.BoundingBoxAsync();
        var fieldBox = await field.BoundingBoxAsync();
        var rightBox = await right.BoundingBoxAsync();

        leftBox.ShouldNotBeNull();
        fieldBox.ShouldNotBeNull();
        rightBox.ShouldNotBeNull();

        var before = fieldBox.X - (leftBox.X + leftBox.Width);
        var after = rightBox.X - (fieldBox.X + fieldBox.Width);

        after.ShouldBe(before, tolerance: 1);
    }
}