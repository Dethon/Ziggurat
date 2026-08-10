using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SendMessage_AppearsInChat()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Hello, this is an E2E test message");
        await chatInput.PressAsync("Enter");

        var userMessage = page.Locator(".message-content", new PageLocatorOptions { HasText = "Hello, this is an E2E test message" });
        await userMessage.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Wait for agent response — the assistant message element may appear early (with empty
        // content during "thinking"), so poll until it has non-empty text. The model sometimes
        // answers even a greeting with a tool call, and that raises an approval prompt which
        // holds the reply hostage until it is answered — reject whatever appears between waits,
        // or no amount of timeout produces text.
        var assistantMessage = page.Locator(".chat-message.assistant .message-content");
        var deadline = DateTime.UtcNow.AddSeconds(90);
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
        }
    }

    internal static async Task SelectUserAndAgentAsync(IPage page, int userIndex = 0)
    {
        // Dismiss any approval-modal-overlay left by the StreamResumeService.
        // When a new page connects, the server may push pending approval state from
        // a previous test's session, showing the overlay and blocking all clicks.
        await DismissApprovalOverlayAsync(page);

        // Select a unique user identity per test to avoid server-side state pollution.
        await OpenUserDropdownAndSelectAsync(page, userIndex);

        var chatInput = page.Locator("textarea.chat-input");

        // The chat input becomes enabled once SignalR is connected and an agent is selected.
        try
        {
            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });
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
        // so it can arrive at any point — including the window between the user-select
        // retry above and this click. Guard the click with the same dismiss-and-retry
        // pattern used for the user-dropdown click.
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
            if (showing is null || !await rejectBtn.IsVisibleAsync())
            {
                return;
            }

            // The buttons stay disabled while an answer is in flight, so the click waits for
            // the modal on screen to be answerable — this one's, or the next one's.
            await rejectBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
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

    // Two overlays can intercept these clicks and make the flow flaky:
    //   * .approval-modal-overlay (z-index 1000) — pushed by StreamResumeService over SignalR
    //     at any moment; it sits above the dropdown items, so the item click is intercepted
    //     until it is dismissed.
    //   * .dropdown-backdrop (full-viewport, painted above the avatar button) — it exists only
    //     while the dropdown is open and intercepts the avatar-button click. Re-clicking the
    //     avatar button while the dropdown is already open therefore never lands.
    //
    // Each attempt dismisses any approval overlay, opens the dropdown ONLY when it is closed
    // (no backdrop present), then clicks the item. On interception it resets to a known-closed
    // state — dismiss overlay, then click the backdrop to close the dropdown — so the next
    // attempt re-opens cleanly.
    private static async Task OpenUserDropdownAndSelectAsync(IPage page, int userIndex)
    {
        var avatarButton = page.Locator(".avatar-button");
        var backdrop = page.Locator(".dropdown-backdrop");
        var dropdownItem = page.Locator(".user-dropdown-item").Nth(userIndex);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await DismissApprovalOverlayAsync(page);

                if (!await backdrop.IsVisibleAsync())
                {
                    await avatarButton.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }

                await dropdownItem.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
                await dropdownItem.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                await DismissApprovalOverlayAsync(page);
                if (await backdrop.IsVisibleAsync())
                {
                    // Close by coordinate, not by locator: the backdrop's own centre sits under
                    // the open menu, so a locator click on it is intercepted by a menu item and
                    // its timeout would escape this recovery. The avatar button's spot is always
                    // covered by the backdrop while the menu is open, so a raw click there lands
                    // on the backdrop and closes the dropdown.
                    var avatarBox = await avatarButton.BoundingBoxAsync();
                    if (avatarBox is not null)
                    {
                        await page.Mouse.ClickAsync(
                            (float)(avatarBox.X + avatarBox.Width / 2),
                            (float)(avatarBox.Y + avatarBox.Height / 2));
                    }
                }
            }
        }
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
        var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });

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


    [SkippableFact]
    public async Task LoadPage_ShowsAvatarPickerAndInput()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeTrue();

        // Chat input should be visible (it may be enabled if the agent auto-selected)
        var chatInput = page.Locator("textarea.chat-input");
        (await chatInput.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task IdleWelcomeScreen_HasNoPerpetualAnimations()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

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
    public async Task SelectUser_AvatarUpdates()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The guarded helper, not a bare click: the approval overlay can be replayed onto this
        // page at any moment by StreamResumeService and would intercept the item click for as
        // long as it sits there.
        await OpenUserDropdownAndSelectAsync(page, fixture.NextUserIndex());

        var avatarImage = page.Locator("img.avatar-image");
        await avatarImage.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        (await avatarImage.IsVisibleAsync()).ShouldBeTrue();

        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeFalse();

        // Wait for state propagation
        await page.WaitForTimeoutAsync(1_000);
    }

    [SkippableFact]
    public async Task ConnectionStatus_ShowsConnected()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

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

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

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
                    var streamingCancel = page.Locator(
                        "button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
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
            var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
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

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

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
            var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
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

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        try
        {
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("Write a very long and detailed story about a space adventure. Do not call any tools, just write the story.");
            await chatInput.PressAsync("Enter");

            // Wait for Cancel button to appear (signals streaming has started)
            var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 40_000 });

            // Cancel replaces Send while the reply runs: one button in that spot, always the one
            // the user can act on.
            var sendButton = page.Locator("button.btn-primary", new PageLocatorOptions { HasText = "Send" });
            await Assertions.Expect(sendButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 5_000 });

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
            await Assertions.Expect(sendButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
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