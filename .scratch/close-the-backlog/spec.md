# Close the backlog: every unwritten, needs-fixture and judge claim runs

The scorecard's coverage field still counts 23 `unwritten`, 5 `needs-fixture` and 3 `judge`
claims. This effort closes them: each claim either gains a citing scenario, a judged check on a
scenario that carries the material, or — where the evidence says the deployment does not follow
the rule — an honest reclassification with the finding written down.

## Ground rules

- One issue per family batch, one commit per resolved issue.
- TDD for every fixture change: the fixture test goes red first.
- Each batch is validated with a filtered armed run
  (`ZIGGURAT_EVAL=1 dotnet test Tests/Tests.csproj --filter "DisplayName~..."`) before its
  exemptions are removed for good; results recorded in the issue.
- A claim that a scenario asserts as its subject is cited; a judgement about a sentence or an
  intent rides as a `JudgedCheck` on the scenario that carries the material.

## Issues

1. `01-timers.md` — listed-by-glob, cancelled-by-removing-it, text-is-spoken (judged), plus
   voice.several-sentences-only-when-asked riding the listing scenario.
2. `02-vault.md` — frontmatter keys, templates, headings, daily notes, irreversible ask,
   text-only writes, no-new-top-level-folder (inbox seed), attachments referenced (seed, no
   binary handling needed — the claim is about the reference).
3. `03-home-assistant.md` — entity named as listed, alarm target+insistent, snooze as a new
   event, written failure in plain words, arguments from help (fan-speed select enum).
4. `04-voice.md` — unclear request acted on, abbreviations spelled out.
5. `05-memory.md` — mechanism never mentioned, outdated facts deleted, noisy store swept
   (per-scenario store, so no shared-fixture cost).
6. `06-web.md` — back is an action (index seed + explicit return), actions chain from the diff
   (wizard page), browse-reads/snapshot-structures judged on the booking scenario.
7. `07-subagents.md` — one research-shaped scenario that reliably delegates (the reflex the
   chronicle runs documented) closes heavy-work, self-contained prompt, no worker named
   (deterministic) and success-criteria + synthesis (judged).
8. `08-voice-acknowledgement.md` — one-word-before-slow-work: assess what the harness would
   need to see the separate emission; close it or re-state the blocking work precisely.
