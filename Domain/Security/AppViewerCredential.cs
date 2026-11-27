namespace Domain.Security;

// Which credential says who a built app's ask or read is for, the one rule both of the agent's doors hold
// (ConversationGroup admitting an ask, AppReader a read): the viewer's assertion, or — a check's alone — the
// agent's own call token for the apps server (CheckCallVerifier), never both, which would leave two answers.
// A request carrying neither is the assertion's refusal (Missing). No verifier for checks believes no check.
public static class AppViewerCredential
{
    public static async Task<TurnAdmission> VerifyAsync(
        string? assertion, string? checkCallToken, string assertionChannel,
        AssertionVerifier assertions, CheckCallVerifier? checks, CancellationToken ct) =>
        (assertion, checkCallToken) switch
        {
            ({ }, { }) => new TurnAdmission.Refused(TurnAdmission.BothAnAssertionAndACheck),
            (null, { } token) => checks is null
                ? new TurnAdmission.Refused(CheckCallVerifier.NoAppsServer)
                : await checks.VerifyAsync(token, ct),
            _ => await assertions.VerifyTurnAsync(assertion, assertionChannel, ct)
        };
}