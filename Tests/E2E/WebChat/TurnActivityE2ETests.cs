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