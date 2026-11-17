using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// An approval, answered either way. Out of WebChatConversationE2ETests by length: as one chain with it
// they were the run's longest.
[Collection(WebChatE2ECollections.Approvals)]
[Trait("Category", "E2E")]
public class WebChatApprovalE2ETests(WebChatE2EFixture fixture)
{
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

            await chatInput.FillAsync(QueueE2E.ToolDemand);
            await chatInput.PressAsync("Enter");
            await approvalModal.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

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
            // A pending approval left server-side would be offered to later tests' pages.
            await DrainPendingApprovalsAsync(page);
        }
    }
}