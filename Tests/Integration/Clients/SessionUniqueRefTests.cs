using System.Text.RegularExpressions;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The browser-dependent half of session-unique numbering: a real page restamped by a real second
// pass hands out numbers above everything the first pass issued, in both namespaces.
[Collection(PlaywrightCollections.SharedBrowser)]
public partial class SessionUniqueRefTests(PlaywrightWebBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ASecondSnapshot_NumbersItsRefsAboveTheFirst()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(sessionId,
                """
                <button id="one">First</button>
                <button id="two">Second</button>
                """);

            var first = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            var second = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));

            var firstNumbers = RefNumbers(first.Snapshot!);
            var secondNumbers = RefNumbers(second.Snapshot!);

            firstNumbers.ShouldNotBeEmpty();
            secondNumbers.ShouldNotBeEmpty();
            // The page did not change; only the numbers moved on. A number, once issued, is
            // never issued again in the session.
            secondNumbers.Min().ShouldBeGreaterThan(firstNumbers.Max());
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ASecondImageStamping_NumbersItsRefsAboveTheFirst()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(sessionId,
                """<img id="pic" src="/pic.png" style="width:300px;height:300px" alt="A chart">""");

            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            var firstRef = await StampedRefAsync(sessionId, "pic");

            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            var secondRef = await StampedRefAsync(sessionId, "pic");

            var firstNumber = int.Parse(firstRef["i-".Length..]);
            var secondNumber = int.Parse(secondRef["i-".Length..]);
            secondNumber.ShouldBeGreaterThan(firstNumber);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private async Task<string> StampedRefAsync(string sessionId, string elementId) =>
        await fixture.Browser.EvaluateOnSessionAsync<string>(
            sessionId,
            $"() => document.getElementById('{elementId}').getAttribute('data-img-ref') ?? ''");

    private async Task PrepareAsync(string sessionId, string markup)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com"));
        nav.Status.ShouldBe(BrowseStatus.Success);

        await fixture.Browser.EvaluateOnSessionAsync(
            sessionId,
            $"() => {{ document.body.innerHTML = {System.Text.Json.JsonSerializer.Serialize(markup)}; }}");
    }

    private static IReadOnlyList<int> RefNumbers(string snapshot) =>
        RefTagRegex().Matches(snapshot)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

    [GeneratedRegex(@"\[ref=e-(\d+)\]")]
    private static partial Regex RefTagRegex();
}