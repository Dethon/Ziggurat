using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SendMessage_AppearsInChat()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Hello, this is an E2E test message");
        await chatInput.PressAsync("Enter");

        // .First, because the agent often quotes the message back and then two bubbles carry the
        // text — a strict-mode violation, which fails outright instead of waiting. The user's own
        // bubble is the earlier of the two in document order.
        var userMessage = page
            .Locator(".message-content", new PageLocatorOptions { HasText = "Hello, this is an E2E test message" })
            .First;
        await userMessage.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Wait for agent response — the assistant message element may appear early (with empty
        // content during "thinking"), so poll until it has non-empty text. The model sometimes
        // answers even a greeting with a tool call, and that raises an approval prompt which
        // holds the reply hostage until it is answered — reject whatever appears between waits,
        // or no amount of timeout produces text.
        // The reply comes from a real model over the network, so what bounds this is the provider's
        // worst case rather than the app's. Three minutes: a queued or slow generation ran past the
        // old ninety seconds and reported it as an empty bubble, which reads as a broken client
        // rather than as the wait it was. The loop still returns the moment text appears, so a
        // normal run pays nothing for the larger cap.
        var assistantMessage = page.Locator(".chat-message.assistant .message-content");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            await DismissApprovalOverlayAsync(page);
            try
            {
                await Assertions.Expect(assistantMessage.First)
                    .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 10_000 });
                break;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                // An approval may have arrived mid-wait; loop to reject it and keep waiting.
            }
            catch (PlaywrightException ex)
            {
                // Past the cap. Say which wait ran out, because "expected not to be empty" on its
                // own has been read as the client failing to render a reply it did receive.
                throw new TimeoutException(
                    "No assistant text after 3 minutes. The reply comes from a real model here, so "
                    + "this is a slow or failed generation rather than the chat client.", ex);
            }
        }
    }

    // DOMContentLoaded rather than NetworkIdle. Every case here goes on to wait for something the
    // app can only show once it has booted and connected — the composer being enabled, a row, the
    // microphone — so waiting for the network to fall quiet first buys nothing and costs its own
    // half-second idle window plus the tail of the WASM payload, on every page the suite opens.
    // The dashboard's pages deliberately keep NetworkIdle: they read counts straight off the
    // document with no locator to wait on.
    internal static Task GotoWebChatAsync(IPage page, string url) =>
        page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    // The page is signed in before it opens (WebChatE2EFixture.CreateSignedInPageAsync), so there is
    // nobody to pick: what is left is the agent and a fresh topic.
    internal static async Task SelectAgentAsync(IPage page)
    {
        // Dismiss any approval-modal-overlay left by the StreamResumeService.
        // When a new page connects, the server may push pending approval state from
        // a previous test's session, showing the overlay and blocking all clicks.
        await DismissApprovalOverlayAsync(page);

        var chatInput = page.Locator("textarea.chat-input");

        // The chat input becomes enabled once the app has booted, SignalR is connected and an
        // agent is selected. The boot is in this wait too, which in a full-suite run is the
        // container serving every other test at the same time, so it is given what the boot and
        // the connect used to be given apiece.
        try
        {
            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 60_000 });
        }
        catch (TimeoutException)
        {
            // Input still disabled — initialization may not have auto-selected an agent;
            // fall through to manually select the first agent in the switcher.
            var firstAgent = page.Locator(".agent-seg:visible").First;
            if (await firstAgent.IsVisibleAsync())
            {
                await firstAgent.ClickAsync();
            }

            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 10_000 });
        }

        // Start a fresh topic so previous test messages don't pollute context.
        // The approval overlay is pushed by StreamResumeService via a fire-and-forget
        // SignalR chain (InitializationEffect → LoadTopicHistoryAsync → TryResumeStreamAsync),
        // so it can arrive at any point — including the window between the wait above and
        // this click. Guard the click with a dismiss-and-retry.
        var newTopicBtn = page.Locator(".hearth-new:visible");
        if (await newTopicBtn.IsVisibleAsync())
        {
            try
            {
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                await DismissApprovalOverlayAsync(page);
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
        }
    }

    internal static Task DismissApprovalOverlayAsync(IPage page) =>
        RejectEveryVisibleApprovalAsync(page, TimeSpan.FromSeconds(15));

    // The one guard for any single click a leaked approval overlay can intercept: dismiss
    // whatever is showing, click, and on interception dismiss and try again. The overlay rides
    // a fire-and-forget resume chain, so it can arrive between the dismissal and the click.
    internal static async Task ClickThroughApprovalsAsync(IPage page, ILocator target)
    {
        for (var attempt = 0; ; attempt++)
        {
            await DismissApprovalOverlayAsync(page);
            try
            {
                await target.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
            }
        }
    }

    // Rows are ordered by LastMessageAt desc and re-render while replies stream, so they reorder
    // between an elementFromPoint aim and the tap that follows — the tap then lands on whichever
    // conversation slid under the coordinate. Wait until the order stops moving before aiming at
    // anything by coordinate, and keep rejecting approval prompts, because .approval-modal-overlay
    // (z-index 1000) covers the whole viewport and swallows the gesture.
    //
    // The rule is unchanged — two readings a second apart that agree, with none of the caller's own
    // replies still streaming — but the wait now falls between them rather than before the first, so
    // a caller whose replies landed long ago is told so on its first look instead of after a second
    // of sleeping. Requiring more than one agreeing pair was tried and reverted: rows that reorder
    // while two replies stream rarely agree twice running, and the case that used to settle in five
    // seconds spent the whole cap.
    //
    // The caller's tag is what says which rows are its own; RowSettle holds why the question is
    // narrowed to them. Waiting on every row in the space instead was the suite's single largest
    // source of wall-clock variance: one sibling's reply that never completes leaves a row pulsing,
    // and every later wait in that space then spent its whole cap before passing anyway.
    internal static Task<string> WaitForRowsToStopMovingAsync(
        IPage page, string tag, [CallerMemberName] string caller = "") =>
        E2ETrace.TimeAsync($"rows-settle:{caller}", () => WaitForRowsToStopMovingCoreAsync(page, tag));

    // Long enough to cover a reply that has to be waited out, short enough that a wait which cannot
    // be satisfied is a cost the run can absorb. Settling is normally under ten seconds.
    private static readonly TimeSpan _rowsSettleCap = TimeSpan.FromSeconds(30);

    private static async Task<string> WaitForRowsToStopMovingCoreAsync(IPage page, string tag)
    {
        var deadline = DateTime.UtcNow + _rowsSettleCap;
        var previous = "";
        while (DateTime.UtcNow < deadline)
        {
            await DismissApprovalOverlayAsync(page);
            var snapshot = await page.EvaluateAsync<string>(
                """
                () => [...document.querySelectorAll('.topic-item')]
                    .map(r => (r.classList.contains('is-streaming') ? '*' : '')
                        + r.querySelector('.topic-name').textContent.trim()).join('|')
                """);
            if (RowSettle.HasSettled(previous, snapshot, tag))
            {
                return snapshot;
            }

            previous = snapshot;
            await page.WaitForTimeoutAsync(1_000);
        }

        return $"{previous} (STILL MOVING at {_rowsSettleCap.TotalSeconds:0}s cap)";
    }

    // The prompt on screen is the oldest request still waiting (ApprovalState.Pending is a
    // queue), so answering one surfaces the next in the same instant and the overlay never
    // hides in between. Rejecting once and expecting a clear screen fails whenever the agent
    // has more than one request outstanding — reject until nothing is left instead.
    //
    // This is cleanup, not an assertion: at the deadline it gives up quietly and leaves the
    // caller's own step to report what a stuck overlay broke.
    private static async Task RejectEveryVisibleApprovalAsync(IPage page, TimeSpan budget)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        var rejectBtn = page.Locator(".btn-reject");

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && await overlay.IsVisibleAsync())
        {
            var showing = await CurrentApprovalIdAsync(page);
            if (showing is null)
            {
                // Nothing is on screen any more — the prompt went away while this was asking, which
                // is the goal rather than a state to keep working at.
                return;
            }

            if (!await rejectBtn.IsVisibleAsync())
            {
                // An overlay is up and carries an id, but is not answerable yet: the modal is
                // mid-render, or its buttons are still disabled while a previous answer is in
                // flight. Returning here left the caller with a covered viewport and called it
                // dismissed, which is how a leaked overlay survived to fail an assertion that had
                // already checked for one. It is a moment in the overlay's life, not a state to
                // give up on, so look again — the deadline above is what bounds this.
                await page.WaitForTimeoutAsync(100);
                continue;
            }

            // The buttons stay disabled while an answer is in flight, so the click waits for
            // the modal on screen to be answerable — this one's, or the next one's.
            try
            {
                await rejectBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // The modal this click aimed at was replaced by the next request's, or answered
                // from under it: the button goes unstable and then detaches, and Playwright's own
                // retries run out against a target that is no longer the one on screen. That is a
                // change of state and not a failure to dismiss, so re-read what is showing now and
                // answer that instead — the deadline is what bounds this, not one click's patience.
                continue;
            }

            await WaitUntilApprovalAnsweredAsync(page, showing, TimeSpan.FromSeconds(10));
        }
    }

    // The id of the request the modal is showing, or null when nothing is on screen — including
    // the case where the prompt goes away while this is asking.
    private static async Task<string?> CurrentApprovalIdAsync(IPage page)
    {
        try
        {
            return await page.Locator(".approval-modal")
                .GetAttributeAsync("data-approval-id", new LocatorGetAttributeOptions { Timeout = 2_000 });
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // Answered means this request is off the screen — either nothing is prompting any more, or
    // the next request in the queue has taken its place. Waiting for "no modal" instead would
    // fail whenever another request was already waiting behind this one.
    private static async Task WaitUntilApprovalAnsweredAsync(IPage page, string approvalId, TimeSpan budget)
    {
        var answered = page.Locator($".approval-modal[data-approval-id='{approvalId}']");
        await Assertions.Expect(answered).ToBeHiddenAsync(
            new LocatorAssertionsToBeHiddenOptions { Timeout = (float)budget.TotalMilliseconds });
    }

    // Drives the conversation to a fully quiesced state before the test exits.
    //
    // Pending approvals live in ApprovalService._pendingApprovals — an in-memory,
    // per-container dictionary keyed by topic that survives across pages for the whole
    // suite run. It is cleared ONLY by an explicit approve/reject (RespondToApprovalAsync)
    // or DeleteTopic; the Cancel button (ChatHub.CancelTopic) does NOT clear it. If a test
    // exits while the agent has issued a (possibly follow-up) request_approval, that entry
    // leaks and StreamResumeService re-raises the overlay on every later test's page.
    //
    // Rejecting calls RespondToApprovalAsync server-side, removing the entry. The agent may
    // then issue another tool call, and several requests can be waiting at once, so loop until
    // no overlay reappears and nothing is streaming instead of relying on the next test to
    // dismiss a stale overlay.
    internal static async Task DrainPendingApprovalsAsync(IPage page)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        var cancelButton = page.Locator("[data-testid=composer-cancel]");

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await overlay.IsVisibleAsync())
            {
                try
                {
                    await RejectEveryVisibleApprovalAsync(page, TimeSpan.FromSeconds(20));
                }
                catch (Exception e) when (e is TimeoutException or PlaywrightException)
                {
                    // A reject that will not click leaves the entry for the next test's
                    // dismissal to clear. This runs in a finally, so it must never replace the
                    // failure the test itself is reporting. A click timeout surfaces as
                    // TimeoutException, but the Expect assertion inside
                    // WaitUntilApprovalAnsweredAsync throws PlaywrightException on ITS timeout,
                    // so both are given up on quietly.
                    return;
                }

                continue;
            }

            // No modal. While the stream is still active the agent may still emit another
            // approval, so keep polling. Once nothing is streaming and no modal reappears
            // within a stable window, the conversation is quiesced server-side.
            if (await cancelButton.IsVisibleAsync())
            {
                await page.WaitForTimeoutAsync(1_000);
                continue;
            }

            await page.WaitForTimeoutAsync(2_000);
            if (!await overlay.IsVisibleAsync() && !await cancelButton.IsVisibleAsync())
            {
                return;
            }
        }
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

    // The proxy sends a page with no session to sign in, and sign-in brings it back to where it was
    // going, as the account whose passkey answered. A real ceremony, against Chromium's virtual
    // authenticator, through the stack's own Caddy and accounts service.
    [SkippableFact]
    public async Task ASignedOutBrowser_SignsInAndComesBackAsThemselves()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var name = $"Passkey-{Guid.NewGuid().ToString("N")[..4]}";
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name);
        var userId = Regex.Match(created, "user (?<id>[0-9a-f]+),").Groups["id"].Value;
        var (_, minted) = await fixture.AccountsAsync("mint-link", "--user", userId);
        var link = minted.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal));

        var page = await fixture.CreatePageAsync();
        await AddVirtualAuthenticatorAsync(page);

        // Enrolling lands the browser on the deployment's front page, signed in.
        await page.GotoAsync(link);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create a passkey" }).ClickAsync();
        await page.WaitForURLAsync(fixture.DefaultSpaceUrl);
        await page.Context.ClearCookiesAsync();

        await page.GotoAsync(fixture.WebChatUrl);
        await page.WaitForURLAsync(url => url.Contains("/accounts/sign-in", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in with a passkey" }).ClickAsync();
        await page.WaitForURLAsync(fixture.WebChatUrl);

        await Assertions.Expect(page.Locator(".signed-in-person"))
            .ToHaveAttributeAsync("title", name, new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 60_000 });
    }

    // A platform authenticator that keeps discoverable credentials and verifies its user, standing
    // in for the phone's; it belongs to the page, so enrolling and signing in share one.
    private static async Task AddVirtualAuthenticatorAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new
            {
                protocol = "ctap2",
                transport = "internal",
                hasResidentKey = true,
                hasUserVerification = true,
                isUserVerified = true
            }
        });
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

    [SkippableFact]
    public async Task ApprovalModal_ApproveFlow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        try
        {
            var chatInput = page.Locator("textarea.chat-input");
            var approvalModal = page.Locator(".approval-modal");

            // The model occasionally answers the demand with plain text and no tool call, and
            // then there is no modal to wait for. Demand again — after the text reply finishes,
            // because Enter is silently ignored while the topic is still streaming.
            for (var attempt = 0; ; attempt++)
            {
                await chatInput.FillAsync("IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. After the tool is called say 'Done' without caring for its result. this is a test");
                await chatInput.PressAsync("Enter");
                try
                {
                    await approvalModal.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                    break;
                }
                catch (TimeoutException) when (attempt < 2)
                {
                    var streamingCancel = page.Locator("[data-testid=composer-cancel]");
                    await Assertions.Expect(streamingCancel).ToBeHiddenAsync(
                        new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });
                }
            }

            var toolName = page.Locator(".tool-name");
            (await toolName.TextContentAsync()).ShouldNotBeNullOrEmpty();

            var approved = (await CurrentApprovalIdAsync(page)).ShouldNotBeNull();
            await page.Locator(".btn-approve").ClickAsync();

            // This request is answered. The screen can still be prompting, because the agent may
            // already have another request waiting behind this one.
            await WaitUntilApprovalAnsweredAsync(page, approved, TimeSpan.FromSeconds(10));

            // Wait for streaming to finish — the Cancel button is only visible while streaming.
            var cancelButton = page.Locator("[data-testid=composer-cancel]");
            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });

            // Any assistant bubble with rendered text is the answer. Whether the answer shares the
            // tool call's bubble depends on the message ids the agent's chunks carry, so don't
            // assume it is the first one. A bubble holding only a tool call still renders a
            // .message-content div; it can hold whitespace (so `:not(:empty)` matches it) while
            // collapsing to zero height, which is why the match must also be visible.
            var assistantMessage = page.Locator(".chat-message.assistant .message-content:not(:empty):visible").First;
            await Assertions.Expect(assistantMessage)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        }
        finally
        {
            // Resolve any follow-up approval the agent issued so no server-side
            // pending approval leaks into later tests (see DrainPendingApprovalsAsync).
            await DrainPendingApprovalsAsync(page);
        }
    }

    [SkippableFact]
    public async Task ApprovalModal_DenyFlow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        try
        {
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. Do NOT write any text, just call the tool.");
            await chatInput.PressAsync("Enter");

            var approvalModal = page.Locator(".approval-modal");
            await approvalModal.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            var rejected = (await CurrentApprovalIdAsync(page)).ShouldNotBeNull();
            await page.Locator(".btn-reject").ClickAsync();

            await WaitUntilApprovalAnsweredAsync(page, rejected, TimeSpan.FromSeconds(10));

            // Stream should stop — Cancel button disappears (only visible while streaming)
            var cancelButton = page.Locator("[data-testid=composer-cancel]");
            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30_000 });
        }
        finally
        {
            // The agent often retries the rejected tool, leaving a fresh server-side
            // pending approval. Drain it so it can't pollute later tests' pages.
            await DrainPendingApprovalsAsync(page);
        }
    }

    [SkippableFact]
    public async Task CancelStreaming_StopsResponse()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        try
        {
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("Write a very long and detailed story about a space adventure. Do not call any tools, just write the story.");
            await chatInput.PressAsync("Enter");

            // Wait for Cancel button to appear (signals streaming has started)
            var cancelButton = page.Locator("[data-testid=composer-cancel]");
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 40_000 });

            // Cancel replaces Send while the reply runs: one button in that spot, always the one
            // the user can act on.
            var sendButton = page.Locator("[data-testid=composer-send]");
            await Assertions.Expect(sendButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 5_000 });
            // The composer is empty once the message has gone, so the control that comes back when
            // the stream ends is the microphone rather than Send — one control in that spot, always
            // the one the person is about to use.
            var microphone = page.Locator("[data-testid=dictation-mic]");

            // .approval-modal-overlay covers the whole viewport, so a prompt on screen swallows
            // this click for as long as it is up. It can belong to this turn (the agent asked for
            // a tool despite the prompt) or to another topic whose approval leaked and is being
            // re-raised by StreamResumeService — either way the click has to clear it first.
            try
            {
                await cancelButton.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                await DismissApprovalOverlayAsync(page);

                // Rejecting a prompt that belonged to this turn ends the stream by itself, which
                // is the state this test is asking for; only click when there is still a stream.
                if (await cancelButton.IsVisibleAsync())
                {
                    await cancelButton.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }
            }

            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
            await Assertions.Expect(microphone).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        }
        finally
        {
            // Cancelling does not clear a pending approval server-side (ChatHub.CancelTopic leaves
            // ApprovalService._pendingApprovals alone), so anything this turn raised has to be
            // answered here or it re-raises on every later test's page.
            await DrainPendingApprovalsAsync(page);
        }
    }
}