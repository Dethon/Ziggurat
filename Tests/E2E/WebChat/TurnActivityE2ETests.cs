using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.QueueE2E;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// What a reply did before its answer is one foldable group above the answer, never bubbles: each tool
// call with its arguments and its answer. Read back after a reload, from the kept turn, the group is
// what streamed.
[Collection(WebChatE2ECollections.Approvals)]
[Trait("Category", "E2E")]
public class TurnActivityE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AToolTurn_IsOneGroupAboveItsAnswer_AndAReloadDrawsTheSameGroup()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var topic = $"Activity {tag}";

        try
        {
            await HoldOnAnApprovalAsync(page, $"{topic}: {ToolDemand}");
            await page.Locator(".btn-approve").ClickAsync();
            await IdleAsync(page);

            var streamed = await OpenedGroupAsync(page);

            // The answer is a bubble of its own under the group; nothing the reply did is one.
            var said = await Conversation(page);
            var asked = said.FindIndex(line => line.StartsWith("user:") && line.Contains($"{topic}:"));
            said.Skip(asked + 1).ToList().ShouldSatisfyAllConditions(
                rest => rest.ShouldContain("activity: Used 1 tool"),
                rest => rest.Last().ShouldStartWith("assistant: Done"));

            // Once the turn is kept, its row's preview is the answer rather than the question.
            var row = page.Locator(".topic-item", new PageLocatorOptions { HasText = topic }).First;
            await Assertions.Expect(row.Locator(".topic-preview"))
                .ToHaveTextAsync(new Regex("Done"), new LocatorAssertionsToHaveTextOptions { Timeout = 60_000 });

            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await DismissApprovalOverlayAsync(page);
            await OpenTopicPastTheOverlayAsync(page, topic);
            await UserMessage(page, topic).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            (await OpenedGroupAsync(page)).ShouldBe(streamed);
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // A page opened while the reply waits on its call (a reload, another device) draws the reply as it
    // stands: one live group with the call running, drawn once, which its answer then completes.
    [SkippableFact]
    public async Task AReloadMidTurn_DrawsTheRunningCallOnce_AndItsAnswerWhenItComes()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var topic = $"Midway {tag}";

        try
        {
            await HoldOnAnApprovalAsync(page, $"{topic}: {ToolDemand}");
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await OpenTopicPastTheOverlayAsync(page, topic);

            // The approval's overlay covers the page, so presses are dispatched on the controls.
            var group = page.Locator(".turn-activity.live");
            await Assertions.Expect(group).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
            await group.Locator(".activity-header").DispatchEventAsync("click");
            var call = group.Locator(".tool-call");
            await Assertions.Expect(call).ToHaveCountAsync(1);
            await Assertions.Expect(call).ToHaveClassAsync(new Regex(@"\brunning\b"));

            await page.Locator(".btn-approve").ClickAsync();
            await IdleAsync(page);

            var done = page.Locator(".turn-activity").Last;
            await Assertions.Expect(done.Locator(".tool-call")).ToHaveCountAsync(1);
            await Assertions.Expect(done.Locator(".tool-call")).ToHaveClassAsync(new Regex(@"\bdone\b"));
            (await Conversation(page)).Last().ShouldStartWith("assistant: Done");
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // A page that drew a call and left the conversation mid-turn (another conversation opened, a phone
    // backgrounded, a reconnect) picks the reply up again from its stream: the call's answer, written
    // while it was away, completes the call it drew rather than leaving it waiting.
    [SkippableFact]
    public async Task LeavingAndComingBackMidTurn_TheCallIsAnsweredWhereItWasDrawn()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var topic = $"Away {tag}";

        try
        {
            await HoldOnAnApprovalAsync(page, $"{topic}: {ToolDemand}");
            await Assertions.Expect(page.Locator(".turn-activity.live .activity-title"))
                .ToContainTextAsync("Running", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

            await page.Locator("button[aria-label='New chat']:visible").First.DispatchEventAsync("click");
            await Assertions.Expect(page.Locator(".turn-activity")).ToHaveCountAsync(0);
            await page.Locator(".btn-approve").ClickAsync();
            await OpenTopicPastTheOverlayAsync(page, topic);
            await IdleAsync(page);

            var group = page.Locator(".turn-activity").Last;
            await group.Locator(".activity-header").ClickAsync();
            await Assertions.Expect(group.Locator(".tool-call")).ToHaveCountAsync(1);
            await Assertions.Expect(group.Locator(".tool-call")).ToHaveClassAsync(new Regex(@"\bdone\b"));
            (await Conversation(page)).Last().ShouldStartWith("assistant: Done");
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // A call the person turned down ends the reply on it: the group shows the call declined, and the line
    // said in its place follows once the turn is kept, as a reload draws it.
    [SkippableFact]
    public async Task ACallTurnedDown_IsDeclinedInItsGroup_WithTheLineSaidInItsPlace()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var topic = $"Declined {tag}";

        try
        {
            await HoldOnAnApprovalAsync(page, $"{topic}: {ToolDemand}");
            await page.Locator(".btn-reject").ClickAsync();
            await IdleAsync(page);

            var group = page.Locator(".turn-activity").Last;
            await Assertions.Expect(group.Locator(".activity-title")).ToHaveTextAsync("Used 1 tool");
            await group.Locator(".activity-header").ClickAsync();
            await Assertions.Expect(group.Locator(".tool-call")).ToHaveClassAsync(new Regex(@"\bdeclined\b"));
            await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").Last)
                .ToContainTextAsync("was not run: it was turned down", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // The last group opened, and its call opened: the call's name, arguments and answer as drawn.
    private static async Task<string> OpenedGroupAsync(IPage page)
    {
        var group = page.Locator(".turn-activity").Last;
        await Assertions.Expect(group.Locator(".activity-title")).ToHaveTextAsync("Used 1 tool");
        await group.Locator(".activity-header").ClickAsync();
        var call = group.Locator(".tool-call");
        await Assertions.Expect(call.Locator(".tool-call-name")).ToContainTextAsync("glob");
        await Assertions.Expect(call).ToHaveClassAsync(new Regex(@"\bdone\b"));
        await call.Locator(".tool-call-header").ClickAsync();
        await Assertions.Expect(call.Locator(".tool-args")).ToContainTextAsync("pattern");
        await Assertions.Expect(call.Locator(".tool-section").Last).Not.ToContainTextAsync("No answer");
        return await group.InnerTextAsync();
    }
}