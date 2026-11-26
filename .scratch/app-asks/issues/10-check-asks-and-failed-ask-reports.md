# 10: Check asks and failed-ask reports

Spec: `.scratch/app-asks/spec.md` ("Checks"; "Runtime errors").

**What to build:** During `./check`, the page's asks are real turns that act for the author. They use
the check's own app session and call token, against the preview's manifest and instructions, and
count toward the cap. The check's summary lists failed asks. In a real browser, a failed ask is
reported to the app's runtime errors, reduced for a member.

**Blocked by:** 02, 07

**Status:** done

- [x] A check's ask is admitted on the agent's own call token for the apps server, naming the author. Both headers together, the guest, or another server's token are refused (agent seam).
- [x] A check's asks count toward the app's cap.
- [x] The check summary lists failed asks (code and message) beside refused reads.
- [x] The client library reports a failed ask to `/_errors`. A member's report is reduced to the ask's error code, and the author's keeps the message.

## Comments

**Done (2026-11-26).** What 11 builds on:

- **The wire**: `ChannelMessageNotification.CheckCallToken` (`checkCallToken`, mapped onto `ChannelMessage`), sent by the
  apps server **in place of** `Assertion` from a check's session (`ViewerProof.CheckCall` now carries the call's
  `call.Household` beside its token; `AppSessions.OpenCheck`, `CheckRequest` take it; a `./check` call proving no
  household runs no check).
- **Admission** (`ConversationGroup.PersonOfAsync`; `ChatMonitor`'s optional `CheckCallVerifier`, which the agent's
  host registers, `InjectorModuleTests.ConfigureAgents_BelievesAChecksAsks`): both together refused
  (`TurnAdmission.BothAnAssertionAndACheck`, now shared with `AppReader`); token alone verified by
  `CheckCallVerifier` (agent's key, in date, audience `CallTo(sharedServers.apps)`, a person; guest, nobody,
  another server's, another key's, out of date refused); `AppTurns.Believed` drops the token off anything but an
  app turn on the deployment's `apps` endpoint. The identity's channel is **restamped `apps`**, so the ask's
  calls are signed and its usage counted on `apps` as any ask's. Then standing, the `apps` feature and the
  agent, unchanged. Every refusal is `not-verified`. Documented as the one exception in `person-assertions.md`.
- **The ask**: preview's tree manifest/instructions/asks/model as before; the cap read on the check's household
  and counted as anyone's; waited on `min(Asks:Timeout, what the check's session has left)`, on the asks' clock.
- **`failure`** (`AppAsks.FailureField`, `AppAsks.Failed`): a failed turn's own words (not a `RefusalAnswers`
  sentence: the agent's exception, often a provider body such as a strict-schema refusal) ride on the envelope
  **on the preview alone**, clipped like `reply`. This changes the earlier "logged, never shown" rule for the
  preview only (the author's own; chat already shows a person their own failed turn's words).
- **Check report**: `viewports[].failedAsks` = `{status, text?, errorCode, message?, hint?, reply?, failure?}` for a
  `/_ask` on the preview answered with the envelope at any status (a non-envelope answer stays a failed request);
  its console line dropped. Summary: `N failed asks` in the counts and
  `failed ask: <code>: <message> (the agent said: ...) (its reply: ...)`, clipped. Exit code unchanged.
- **No text**: `AppAsks.Unanswered`'s hint now says the assistant may be stopping on tools needing an approval an
  ask cannot get (the refused-calls bound itself is unchanged: it still counts calls, not rounds).
- **Runtime errors**: kind `ask` (`AppErrors.Kinds.Ask`, `AppErrorAsk { Envelope }`, strict: no text, no `reply`);
  the client library reports a failed `AskError` once (never again as a rejection), with the envelope's code,
  message and hint; a member's report keeps the code alone (no `name`), the operator's event says
  `<app>: ask: <code>`.
- **For 11**: the `apps` skill still lists error kinds `error`, `rejection`, `read` and the check's outputs
  without failed asks; it should teach `ask` kind, `failedAsks` / `failure` / `reply` in `report.json`, and that
  an ask made on page load runs once per viewport (every viewport reloads the page) and may outlive
  `Checks:Settle`, so asks are better exercised from an assertion file that awaits them.
- **Tests**: `AppTurnAgentTests` (admitted on the token acting as the person on `apps`; both, guest, nobody,
  another server's, another key's, out of date refused; a token on another channel admits nothing; apps off
  refused), `McpChannelConnectionParsingTests`, `InjectorModuleTests`, `AppsAsksTests` (sent on the token with the
  tree's instructions; counted toward the cap; wait bounded by the check; `failure` on the preview alone; a no-text
  turn's hint), `AppChecksTests` (two failed asks in report.json and the summary, not console errors or failed
  requests), `AppsRuntimeErrorsTests` (shape, author's kept, member's reduced), `AppsServingInABrowserTests` (a
  caught refusal reported as an ask). `TestCheckCalls` is the shared check-token helper (was private to
  `AppReadsApiTests`).

