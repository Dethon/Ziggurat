# 01 — An answer is read for what it means

**What to build:** `IApprovalReader` in the voice channel: given the spoken prompt and the transcript, answers Approved / Declined / Ambiguous by the spec's rule — Jev's two nouls through the typed-judgment contract, the word list as agreement partner and as the whole answer when Jev is absent. `RequestApprovalTool` takes it from DI. Settings `approval.judgment: { enabled, deadlineMs, sure, counter, lean }` in `McpChannelVoice/appsettings.json`, `typeSafe` beside them, the key wired on the compose service with a placeholder in `.env`. The metric gains who decided and the probabilities. Test-first with a fake contract; the parser's own tests untouched.

**Blocked by:** `.scratch/jev-skill-preload/issues/02`

**Status:** done

- [x] 0.95/0.03 → Approved; 0.03/0.94 → Declined; 0.03/0.69 (a narrowed "sí, pero…") → Ambiguous even though the word list says Approved.
- [x] 0.88/0.02 with the word list Approved → Approved; the same with the word list Ambiguous → Ambiguous; 0.88/0.02 with the word list Declined → Ambiguous.
- [x] 0.10/0.16 (filler) → Ambiguous whatever the word list says of it.
- [x] Contract absent or past the deadline → the word list's verdict; a fake that answers after the deadline is not waited for.
- [x] `enabled: false` never calls the contract.
- [x] The metric event carries `judgment` / `agreement` / `wordlist` and, when Jev answered, both probabilities.
- [x] The server contract tests pass with the new registration; an empty key makes no call.
- [x] Spec: `.scratch/jev-voice-approval/spec.md` § Decisions.

## Answer

- `McpChannelVoice/Services/IApprovalReader.cs` (`ApprovalReading`: response, who decided, the two
  probabilities, latency) and `JudgedApprovalReader` — two nouls over `{prompt, answer}`, the
  spec's rule, the word list as agreement partner and as the whole answer when the judge is off,
  absent or late. The judge is raced against the deadline (`Task.WaitAsync`), so a judge that
  ignores its token is not waited for. An empty transcript never reaches the judge.
- `RequestApprovalTool` takes the reader from DI and passes the prompt as spoken (the re-ask's
  "No entendí." included). `VoiceEvent` gains `DecidedBy` (`ApprovalDeciders`: judgment /
  agreement / wordlist), `ApprovedProbability`, `DeclinedProbability`; the judge's latency rides
  `DurationMs`.
- Settings: `VoiceSettings.TypeSafe` (`TypeSafeOptions`) and `VoiceSettings.Approval.Judgment`
  (`ApprovalJudgmentSettings`: enabled, deadlineMs 1000, sure 0.9, counter 0.1, lean 0.5) in
  `McpChannelVoice/appsettings.json`. The key reaches the container as `TYPESAFE__APIKEY` through
  the service's `env_file: .env`, the same line the agent and the browse server read — no
  `${TYPESAFE_API_KEY}` entry, for the reason the preload's ticket 02 recorded.
- Registration: `ConfigModule` calls `AddTypeSafeJudge(settings.TypeSafe)`;
  `ApprovalJudgmentRegistrationTests` pins that an empty key resolves a reader over an
  unconfigured judge with no keep-alive.
- Tests: `JudgedApprovalReaderTests` (the rule, the deadline race, disabled, the request's shape),
  `ApprovalSettingsBindingTests` (binding and the shipped values), three new cases in
  `RequestApprovalToolTests` (the reader decides, the metric says who and what). The parser's own
  tests are untouched.
