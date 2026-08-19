# Close the eval backlog

Make every claim the scorecard lists as `unwritten` (23), `needs-fixture` (5) or `judge` (3) run.
The typed backlog in `Tests/Eval/ClaimExemptions.cs` is the work list; removing a line there and
adding the citing scenario (or judged check) is what closing an entry looks like. Kinds `guard`
and `finding` stay: a guard already runs, and the one finding is about the deployment, not the
suite.

## Approach

One issue per family, each resolved by: fixture seeds first (TDD where a fixture changes),
scenarios second, deterministic suite green, then filtered armed runs
(`ZIGGURAT_EVAL=1 dotnet test Tests/Tests.csproj --filter "DisplayName~..."`), one commit per
family. Rubric-shaped claims close as judged checks on the scenario that carries the material —
the coverage test counts Claims ∪ Judged.

Two entries may survive with a rewritten reason instead of a scenario, and only with armed
evidence: the two delegation-quality judge claims if no turn shape reliably delegates (the
standing finding on `subagents.parallel-parts-are-delegated`), and any scenario an armed run shows
the model failing for a reason that is a finding about the deployment rather than the harness.

## Issues

1. `01-timers-and-voice.md` — timers.{listed-by-glob, cancelled-by-removing-it,
   text-is-spoken-never-an-instruction}, voice.{several-sentences-only-when-asked,
   unclear-request-is-acted-on, abbreviations-are-spelled-out, one-word-before-slow-work}
2. `02-vault.md` — vault.{frontmatter-keeps-its-other-keys, templates-are-not-expanded,
   headings-are-referenceable, daily-notes-are-appended-to, irreversible-change-is-asked-about,
   writes-are-text-only, no-new-top-level-folder, attachments-stay-in-their-folder}
3. `03-home-assistant.md` — home.{entity-named-as-listed, arguments-come-from-help,
   exit-codes-are-never-voiced, alarm-carries-target-and-insistent, snooze-is-a-new-event}
4. `04-memory.md` — memory.{mechanism-is-never-mentioned, outdated-facts-are-deleted,
   noisy-store-is-swept}
5. `05-web.md` — web.{back-is-an-action, actions-chain-from-the-diff,
   browse-reads-and-snapshot-structures}
6. `06-subagents.md` — subagents.{heavy-work-is-delegated, prompt-is-self-contained,
   no-worker-is-named, success-criteria-are-stated, answer-is-synthesised}
