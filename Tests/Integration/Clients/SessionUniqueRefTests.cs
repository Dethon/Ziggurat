using System.Text.RegularExpressions;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The browser-dependent half of session-unique numbering: a real page restamped by a real second
// pass hands out numbers above everything the first pass issued, in both namespaces.
[Collection(PlaywrightCollections.IsolatedSessions)]
public partial class SessionUniqueRefTests(IsolatedSessionBrowserFixture fixture)
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
            // The picture must actually load. An address that 404s renders at its declared size
            // only until the request fails; once it does, Firefox stops honouring the CSS box and
            // the element collapses to its alt text — under a hundred pixels, so the second pass
            // filters it out as furniture and erases the ref the first pass stamped. That made
            // this test fail on whether the 404 had come back yet. A data URI decodes instantly
            // and cannot fail.
            await PrepareAsync(sessionId,
                $"""<img id="pic" src="{OnePixelPngDataUri}" style="width:300px;height:300px" alt="A chart">""");
            await WaitForImageToLoadAsync(sessionId, "pic");

            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            // A fresh session's first pass stamps i-1; a second pass over the same one-picture
            // page must number above it, never reissue it. Observed through the contract: the
            // first number answers the superseded wall (renumbered, not reused) and the second
            // answers the picture.
            var fetched = await fixture.Browser.FetchImagesAsync(
                new ImageFetchRequest(sessionId, ["i-1", "i-2"]));

            fetched.Images.Count.ShouldBe(2);
            fetched.Images[0].Status.ShouldBe(ImageFetchStatus.RefSuperseded);
            fetched.Images[1].Status.ShouldBe(ImageFetchStatus.Success);
            fetched.Images[1].Label.ShouldBe("A chart");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // A one-pixel PNG, stretched by CSS to clear the size filter. Inline so the picture is present
    // the moment the markup is, with no request to lose.
    private const string OnePixelPngDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // The measurement reads the rendered box, so it must not run while the picture is still
    // arriving: a decoded image and an undecoded one are different sizes.
    private Task WaitForImageToLoadAsync(string sessionId, string elementId) =>
        fixture.Browser.EvaluateOnSessionAsync(
            sessionId,
            $$"""
              async () => {
                  const img = document.getElementById('{{elementId}}');
                  if (img.complete && img.naturalWidth > 0) return;
                  await new Promise(r => { img.onload = r; img.onerror = r; });
              }
              """);

    private async Task PrepareAsync(string sessionId, string markup)
    {
        // Partial counts: example.com is a blank anchor to inject onto, not the subject. A
        // DOMContentLoaded that never arrives spends the production 30s budget and returns Partial
        // with a usable page, and demanding Success turned that into a failure of whatever the case
        // was really asserting.
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com"));
        nav.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

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