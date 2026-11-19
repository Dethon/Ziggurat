using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.QueueE2E;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Steer now: a reply held on its tool's approval is steered with a
// queued message, and once the tool has run the reply ends there, without asking the model again; the
// steer follows it as the person's next message and is answered. The held approval is the tool
// boundary: the steer is in before the tool returns, so the cut cannot miss it.
[Collection(WebChatE2ECollections.People)]
[Trait("Category", "E2E")]
public class SteerNowE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task ASteeredReply_EndsAfterItsToolCall_AndTheSteerIsAnsweredNext()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var steerWords = $"Reply with only the word STEER{tag}. Do not call any tools.";

        try
        {
            await HoldOnAnApprovalAsync(page,
                $"Steer {tag}: IMPORTANT: You MUST call a tool right now, before writing anything. Use your file " +
                $"search/glob tool to find all files with pattern **/*. Only once the tool has returned, reply " +
                $"with the word AFTER{tag} and nothing else. this is a test");

            await SendAsync(page, steerWords);
            var chip = page.Locator("[data-testid=queue-chip]", new PageLocatorOptions { HasText = $"STEER{tag}" });
            await Assertions.Expect(chip).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

            // The reply is the reader's own, so their chip offers Steer now. The approval's overlay covers
            // the composer, so the press is dispatched on the button itself.
            var steer = chip.Locator("[data-testid=queue-chip-steer]");
            await Assertions.Expect(steer).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await steer.DispatchEventAsync("click");

            // Steered, it waits for the running reply's end as a chip marked steering, with nothing left
            // to do to it, and goes once that end draws it.
            await Assertions.Expect(chip.Locator("[data-testid=queue-chip-steering]"))
                .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await Assertions.Expect(chip.Locator(".queue-chip-action")).ToHaveCountAsync(0);

            await page.Locator(".btn-approve").ClickAsync();
            await Assertions.Expect(chip).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 180_000 });

            await WaitForAsync(page, () => AnswerTo(page, $"STEER{tag}"), "the steer to be answered");
            await Assertions.Expect(page.Locator("[data-testid=composer-cancel]"))
                .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 180_000 });

            // Split around the steer: the first reply, then the steer, then its answer; and the first
            // reply never got to say what it was told to say after its tool, since the model was not
            // asked again.
            var said = await Conversation(page);
            var first = said.FindIndex(line => line.StartsWith("user:") && line.Contains($"Steer {tag}:"));
            var steered = said.IndexOf($"user: {steerWords}");
            first.ShouldBeGreaterThanOrEqualTo(0, string.Join("\n", said));
            steered.ShouldBeGreaterThan(first + 1, string.Join("\n", said));
            said.Skip(first + 1).Take(steered - first - 1).ShouldAllBe(line => line.StartsWith("assistant:"));
            said.Skip(steered + 1).ShouldContain(line => line.StartsWith("assistant:") && line.Contains($"STEER{tag}"));
            said.ShouldNotContain(line => line.Contains($"AFTER{tag}") && line.StartsWith("assistant:"));
            said.Count(line => line.Contains($"word STEER{tag}")).ShouldBe(1);
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // Two messages steered into the same reply go in together: both are drawn where the reply was cut, one
    // after the other, and answered once. Each asks for a word of its own, so a single answer naming both
    // shows the model was asked once with both in view; answered one by one, the first would be answered
    // before the second was drawn. A fresh load of the conversation reads the same.
    [SkippableFact]
    public async Task TwoSteersIntoOneReply_AreDrawnTogether_AndAnsweredOnce()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);
        var topic = $"Together {tag}";
        var oneWords = $"Reply with the word ONE{tag}. Do not call any tools.";
        var twoWords = $"And in the same reply, after it, the word TWO{tag}. Do not call any tools.";

        try
        {
            await HoldOnAnApprovalAsync(page,
                $"{topic}: IMPORTANT: You MUST call a tool right now, before writing anything. Use your file " +
                $"search/glob tool to find all files with pattern **/*. Only once the tool has returned, reply " +
                $"with the word AFTER{tag} and nothing else. this is a test");

            foreach (var (words, word) in new[] { (oneWords, $"ONE{tag}"), (twoWords, $"TWO{tag}") })
            {
                await SendAsync(page, words);
                var chip = page.Locator("[data-testid=queue-chip]", new PageLocatorOptions { HasText = word });
                var steer = chip.Locator("[data-testid=queue-chip-steer]");
                await Assertions.Expect(steer).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
                await steer.DispatchEventAsync("click");
                await Assertions.Expect(chip.Locator("[data-testid=queue-chip-steering]"))
                    .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            }

            await page.Locator(".btn-approve").ClickAsync();
            await Assertions.Expect(page.Locator("[data-testid=queue-chip]"))
                .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 180_000 });
            await WaitForAsync(page, () => AnswerTo(page, $"TWO{tag}"), "the steers to be answered");
            await IdleAsync(page);

            AnsweredOnceAfterBoth(await Conversation(page), topic, oneWords, twoWords, tag);

            var fresh = await page.Context.NewPageAsync();
            await GotoWebChatAsync(fresh, fixture.WebChatUrl);
            await SelectAgentAsync(fresh);
            await OpenTopicPastTheOverlayAsync(fresh, topic);
            await UserMessage(fresh, $"TWO{tag}").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await WaitForAsync(fresh, () => AnswerTo(fresh, $"TWO{tag}"), "the answer to be loaded");
            AnsweredOnceAfterBoth(await Conversation(fresh), topic, oneWords, twoWords, tag);
        }
        finally
        {
            await DrainPendingApprovalsAsync(page);
        }
    }

    // The steers one after the other, right after the cut reply, then one answer naming both words.
    private static void AnsweredOnceAfterBoth(List<string> said, string topic, string oneWords, string twoWords, string tag)
    {
        var all = string.Join("\n", said);
        var first = said.FindIndex(line => line.StartsWith("user:") && line.Contains($"{topic}:"));
        var one = said.IndexOf($"user: {oneWords}");
        var two = said.IndexOf($"user: {twoWords}");
        first.ShouldBeGreaterThanOrEqualTo(0, all);
        one.ShouldBeGreaterThan(first, all);
        two.ShouldBe(one + 1, all);
        said.Skip(first + 1).Take(one - first - 1).ShouldAllBe(line => line.StartsWith("assistant:"), all);
        var answers = said.Skip(two + 1).ToList();
        answers.ShouldHaveSingleItem(all).ShouldStartWith("assistant:", Case.Sensitive, all);
        answers[0].ShouldContain($"ONE{tag}", Case.Sensitive, all);
        answers[0].ShouldContain($"TWO{tag}", Case.Sensitive, all);
    }
}