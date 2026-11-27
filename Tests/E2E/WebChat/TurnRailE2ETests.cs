using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.QueueE2E;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// The rail beside a conversation: a strip per turn, the strips of the turns on screen lit, a press on one
// taking the reader to that turn's question, and a pill back to the end once they have scrolled away.
// In the list-scroll collection: one more short chain beside the sheet's scrolling case.
[Collection(WebChatE2ECollections.ListScroll)]
[Trait("Category", "E2E")]
public sealed class TurnRailE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task OnADesktop_TheRailJumpsToATurn_PreviewsIt_AndThePillComesBackToTheEnd()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await page.SetViewportSizeAsync(1280, 640);
        var words = await ThreeTurnsAsync(page);

        var track = page.Locator(".turn-rail-track");
        await Assertions.Expect(page.Locator(".turn-rail-mark")).ToHaveCountAsync(3);
        await Assertions.Expect(page.Locator(".scroll-to-end")).ToBeHiddenAsync();

        // Pointing at the middle of the rail previews the second turn: what was asked and its answer.
        var rail = (await track.BoundingBoxAsync())!;
        await track.HoverAsync(new LocatorHoverOptions { Position = new Position { X = 6, Y = rail.Height / 2 } });
        await Assertions.Expect(page.Locator(".turn-rail-preview .asked")).ToContainTextAsync(words[1]);
        await Assertions.Expect(page.Locator(".turn-rail-preview .answered")).ToContainTextAsync(words[1]);

        await track.ClickAsync(new LocatorClickOptions { Position = new Position { X = 6, Y = 1 } });
        await WaitForQuestionAtTheTopAsync(page, words[0]);
        await WaitForLitAsync(page, [true, false, false]);
        await Assertions.Expect(page.Locator(".scroll-to-end")).ToBeVisibleAsync();

        // Next turn steps from the turn being read to the one after it.
        await page.Locator(".turn-rail-nav.next").ClickAsync();
        await WaitForQuestionAtTheTopAsync(page, words[1]);

        await page.Locator(".scroll-to-end").ClickAsync();
        await WaitForTheEndAsync(page);
        await Assertions.Expect(page.Locator(".scroll-to-end")).ToBeHiddenAsync();
        await WaitForLitAsync(page, [false, false, true]);
    }

    // A phone has no pointer to hover with: the rail is a thin one at the edge, a tap on a strip jumps to
    // its turn, and no preview is drawn.
    [SkippableFact]
    public async Task OnAPhone_TappingAStripJumpsToItsTurn()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex(), hasTouch: true, isMobile: true);
        var words = await ThreeTurnsAsync(page);

        var rail = (await page.Locator(".turn-rail-track").BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(rail.X + rail.Width / 2, rail.Y + 1);

        await WaitForQuestionAtTheTopAsync(page, words[0]);
        await WaitForLitAsync(page, [true, false, false]);
        await Assertions.Expect(page.Locator(".turn-rail-preview")).ToBeHiddenAsync();

        var pill = (await page.Locator(".scroll-to-end").BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(pill.X + pill.Width / 2, pill.Y + pill.Height / 2);
        await WaitForTheEndAsync(page);
    }

    // Three answered turns, each row made tall so the conversation overflows whatever the viewport: the
    // claims are about which turns are on screen, and three short replies fit on any of them.
    private async Task<string[]> ThreeTurnsAsync(IPage page)
    {
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);

        var tag = Guid.NewGuid().ToString("N")[..4];
        string[] words = [$"Rail{tag}One", $"Rail{tag}Two", $"Rail{tag}Three"];
        foreach (var word in words)
        {
            await SendAsync(page, $"Reply with only the word {word}");
            await WaitForAsync(page, () => AnswerTo(page, word), $"the answer {word}");
            await IdleAsync(page);
        }

        await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = ".messages-area > * { min-height: 420px; }" });
        var overflows = await page.EvaluateAsync<bool>(
            "() => { const a = document.querySelector('.messages-area'); return a.scrollHeight > a.clientHeight * 3; }");
        overflows.ShouldBeTrue("the conversation does not overflow, so the rail's claims would prove nothing");

        await page.EvaluateAsync("() => { const a = document.querySelector('.messages-area'); a.scrollTop = a.scrollHeight; }");
        await WaitForTheEndAsync(page);
        return words;
    }

    // The turn's first row, not its bubble: the rows are stretched, and a person's bubble sits at the foot
    // of its row.
    private static Task WaitForQuestionAtTheTopAsync(IPage page, string word) =>
        page.WaitForFunctionAsync(
            """
            word => {
                const area = document.querySelector('.messages-area');
                const asked = [...area.querySelectorAll('[data-turn]')].find(row => row.innerText.includes(word));
                if (!asked) return false;
                const offset = asked.getBoundingClientRect().top - area.getBoundingClientRect().top;
                return offset >= 0 && offset < 80;
            }
            """,
            word,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

    private static Task WaitForTheEndAsync(IPage page) =>
        page.WaitForFunctionAsync(
            "() => { const a = document.querySelector('.messages-area'); return a.scrollHeight - a.scrollTop - a.clientHeight <= 2; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

    private static Task WaitForLitAsync(IPage page, bool[] lit) =>
        page.WaitForFunctionAsync(
            """
            lit => {
                const marks = [...document.querySelectorAll('.turn-rail-mark')];
                return marks.length === lit.length && marks.every((m, i) => (m.dataset.inView === 'true') === lit[i]);
            }
            """,
            lit,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
}