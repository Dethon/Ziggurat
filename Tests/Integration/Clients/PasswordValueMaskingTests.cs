using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// What is typed into a password field never comes back out of a browse tool: not in a snapshot,
// not in the diff an action answers with — whoever typed it, the model, the person or the page.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class PasswordValueMaskingTests(IsolatedSessionBrowserFixture fixture)
{
    private const string Secret = "hunter2-correct-horse";

    private const string Form =
        """
        <form>
          <label>Username <input type="text" id="user"></label>
          <label>Password <input type="password" id="pass"></label>
          <label>Confirm <input type="password" id="confirm"></label>
        </form>
        """;

    private static string RefOf(string snapshot, string name) =>
        snapshot.Split('\n').First(l => l.Contains($"\"{name}\"")).Split("[ref=")[1].Split(']')[0];

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task APasswordFilledThroughAnAction_IsMaskedInItsDiff_AndAnEmptyOneStaysEmpty()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await HermeticPage.PrepareAsync(fixture.Browser, sessionId, Form);
            var before = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));

            var filled = await fixture.Browser.ActionAsync(new WebActionRequest(
                sessionId, RefOf(before.Snapshot!, "Password"), WebActionType.Fill, Secret));
            var user = await fixture.Browser.ActionAsync(new WebActionRequest(
                sessionId, RefOf(before.Snapshot!, "Username"), WebActionType.Fill, "fran"));

            filled.Status.ShouldBe(WebActionStatus.Success);
            filled.Snapshot.ShouldNotBeNull().ShouldNotContain(Secret);
            filled.Snapshot.ShouldContain(AccessibilitySnapshotMask);
            // Ordinary text fields still say what they hold.
            user.Snapshot.ShouldNotBeNull().ShouldContain(": fran");

            var after = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            after.Snapshot.ShouldNotBeNull().ShouldNotContain(Secret);
            var confirm = after.Snapshot.Split('\n').First(l => l.Contains("\"Confirm\""));
            confirm.ShouldNotContain(AccessibilitySnapshotMask);
            confirm.ShouldNotContain(":");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task APasswordThePagesOwnScriptFilled_IsMaskedInASnapshot()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await HermeticPage.PrepareAsync(fixture.Browser, sessionId, Form);
            await fixture.Browser.EvaluateOnSessionAsync(sessionId,
                $"() => {{ document.getElementById('pass').value = '{Secret}'; }}");

            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));

            snapshot.Snapshot.ShouldNotBeNull().ShouldNotContain(Secret);
            snapshot.Snapshot.Split('\n').First(l => l.Contains("\"Password\""))
                .ShouldContain(AccessibilitySnapshotMask);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private const string AccessibilitySnapshotMask = AccessibilitySnapshotService.MaskedPassword;
}