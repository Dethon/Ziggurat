using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection(WebChatE2ECollections.Topics)]
[Trait("Category", "E2E")]
public class WebChatTopicManagementE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SelectTopic_LoadsMessages()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // A conversation outlives the run that made it, and the user this test is handed comes off a
        // counter that restarts every run — so a later run can be shown the rows an earlier one left
        // behind under the same name. Two rows reading "Topic one" is a strict-mode violation, which
        // fails outright rather than waiting, and it fails on the run that collided rather than the
        // one that seeded it. A per-run tag is what the gesture suites already use for this.
        var tag = Guid.NewGuid().ToString("N")[..4];

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync($"Topic one {tag} message for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-new:visible"));

        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync($"Topic two {tag} message for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // The wait above only proves the typed bubble rendered — topic two's reply is still
        // streaming, and every chunk bumps its LastMessageAt, which is the key the rows are
        // ordered by. A click aimed at "Topic one" then lands on whichever row slid under it,
        // and when that row is topic two, HandleTopicClick sees the selection is unchanged and
        // dispatches nothing: the click "succeeds" and the messages never switch. Wait for the
        // order to stop moving first.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        // Can't rely on position — other tests' topics may also be visible.
        var topic1 = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Topic one {tag}" });
        await topic1.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // .First, because the agent often quotes the message back and then two bubbles carry the
        // text — a strict-mode violation, which fails outright instead of waiting.
        //
        // A row can still move under a late approval or a resumed stream, and a miss is silent,
        // so the click is retried when the messages didn't switch.
        var messageContent = page
            .Locator(".message-content", new PageLocatorOptions { HasText = $"Topic one {tag} message for E2E" }).First;
        for (var attempt = 0; ; attempt++)
        {
            await WebChatE2ETests.ClickThroughApprovalsAsync(page, topic1);
            try
            {
                await messageContent.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 2)
            {
            }
        }
    }

    // The stack serves one row a page, so a second conversation is only reachable if the sidebar
    // asks for the page below its cursor. Real-browser scroll behaviour with rows reordering
    // underneath is the case a fake cannot reproduce.
    [SkippableFact]
    public async Task ScrollingTheSidebar_ReachesAConversationBelowTheFirstPage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        var user = fixture.NextUserIndex();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectUserAndAgentAsync(page, user);

        var tag = Guid.NewGuid().ToString("N")[..4];

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync($"Paged one {tag} for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-new:visible"));
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync($"Paged two {tag} for E2E — answer in one short sentence.");
        await chatInput.PressAsync("Enter");
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // The rows reorder under every streamed chunk, and a scroll aimed at a moving list lands
        // wherever the list happens to be.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        // The reload is the point: it is the only way to see what one page of the sidebar
        // actually holds, because both conversations were created in this client and are held
        // whether they were paged to or not.
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectUserAndAgentAsync(page, user);

        await page.EvaluateAsync(
            "() => { const r = document.querySelector('.hearth-rows'); if (r) r.scrollTop = r.scrollHeight; }");

        var older = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Paged one {tag}" });
        await older.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    // The title only shows in the top bar on a phone, and that is where it is renamed: tap it,
    // type over it, press Enter. The conversation list is the proof the new name stuck.
    [SkippableFact]
    public async Task RenameTopic_FromTheMobileHeader_RenamesTheConversation()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Unique per run, for the same reason as the topic rows above: the name this test settles on
        // is the one it then looks up, and an earlier run's row carrying it would match too.
        var tag = Guid.NewGuid().ToString("N")[..4];
        var renamedTo = $"Renamed {tag} in E2E test";

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync($"Topic to rename {tag} in E2E test — answer in one short sentence.");
        await chatInput.PressAsync("Enter");

        // Renaming while the reply is still streaming races the read-marker save over the same
        // topic key, so let the turn finish first — the race is the stack's, not this feature's.
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        var headerName = page.Locator(".header-conversation-name");
        await Assertions.Expect(headerName).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, headerName);

        var editor = page.Locator(".header-conversation-edit");
        await Assertions.Expect(editor).ToBeFocusedAsync(new LocatorAssertionsToBeFocusedOptions { Timeout = 5_000 });
        await editor.FillAsync(renamedTo);
        await editor.PressAsync("Enter");

        await Assertions.Expect(headerName).ToHaveTextAsync(
            renamedTo, new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });

        var renamedRow = page.Locator(".topic-item", new PageLocatorOptions { HasText = renamedTo });
        await renamedRow.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task DeleteTopic_RemovesFromSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Unique per run: this one deletes the row it finds, so matching an earlier run's row would
        // delete somebody else's conversation and then assert against the survivor.
        var tag = Guid.NewGuid().ToString("N")[..4];

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync($"Topic to delete {tag} in E2E test — answer in one short sentence.");
        await chatInput.PressAsync("Enter");

        var ourTopic = page.Locator(".topic-item", new PageLocatorOptions { HasText = $"Topic to delete {tag}" });
        await ourTopic.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // The same dismiss-and-retry guard as the other helpers: a pending approval leaked by a
        // sibling test can raise the full-viewport overlay at any moment and intercept these
        // clicks.
        //
        // A row renders the delete button or the confirm pair and never both, so an attempt has to
        // ask which state the row is in rather than assume it is back where the last attempt
        // started. Retrying both clicks together looks equivalent and is not: the first click has
        // already put the row in confirm mode, so every later attempt waited out its timeout on a
        // button that cannot come back, and one intercepted confirm click failed the test three
        // attempts later for the wrong reason.
        var confirmDelete = ourTopic.Locator(".confirm-delete-btn");
        for (var attempt = 0; ; attempt++)
        {
            await WebChatE2ETests.DismissApprovalOverlayAsync(page);
            try
            {
                if (await confirmDelete.CountAsync() == 0)
                {
                    await ourTopic.Locator(".delete-btn").ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }

                await confirmDelete.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                // Overlay re-armed between dismissal and a click; loop to dismiss and retry.
            }
        }

        await Assertions.Expect(ourTopic).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }
}