# Handoff — prompt skills, 2026-09-08

Branch `skills`, four commits on top of `f48903253`: tickets 01–04 done (`issues/01`–`04`
are `Status: done`), review fixes in `eb4ea6504`, whole non-eval suite green (5,309). Tickets
05–10 untouched, `ready-for-agent`. Ticket 11 is new and goes **first**.

## What the eval said

- Demonstrated red for the watches body is on file:
  `.eval-output/scorecard-full.2026-09-08.watches-body-deleted.json` (every run still loaded
  the skill; four body claims 0/3 or 0/6).
- Full passes: base 73/73 (`~/ziggurat-eval-backups/scorecard-full.2026-09-08.base-before-prompt-skills.json`),
  after 02 73/73 (`…after-watches-skill-b.json`), after 04 72/73
  (`.eval-output/scorecard-full.2026-09-08.after-setup-index-file-b.json`, the red a voice probe).
- The one real effect of the move: the model calls `load_skill` as a warm-up probe, first call
  of the turn, on ~7 of 228 runs — garbage names or `home-watches` on turns that need no skill,
  including vault, timer and voice turns, so it is the known probe reflex
  (`ClaimExemptions` → `WebBrowsingPrompt.NoProbeCalls`) with a cheaper target, not the
  watches description in particular. Two description tightenings changed nothing.

## Decisions taken with Francisco

- **Accept the spurious loads at or under 5% of runs.** Judge a move on the family it touched
  and on scenarios that failed their threshold; single-run claim rates always "fall" somewhere
  between two passes at 2-of-3, so the literal "no claim rate falls" is not decidable.
- **Try the tool's shape first (ticket 11)**: our description and an `enum` schema on
  `skillName`, through the `SkillsProvider` seam. Then continue with 05.
- When 05 adds `home-assistant` beside `home-watches`, note the per-skill spurious counts in
  the gate: loads spread across both on unrelated turns = reflex; loads staying on
  `home-watches` on home turns = description, reword it.

## Things the next session should know

- `SkillsProvider` wraps the framework's `AgentSkillsProvider` because the framework has no
  switch for the resource/script tools; the wrapper filters `AIContext.Tools`. The advertisement
  is rendered through the real provider in `PromptSnapshotTests` (`SkillsProvider.AdvertisementAsync`).
- `load_skill` is auto-approved in `ToolApprovalChatClient._alwaysApproved` and recorded by the
  eval under its own name; `EvalTools.LoadSkill`.
- The trigger claim lives on the skill declaration (`HomeWatchesSkill.LoadsForAWatchRequest`,
  first in its `Claims`), not on the `skills` section — the spec's "the section carries the
  trigger claims" sentence is the inconsistent one; ADR 0039 and the glossary agree with the code.
- Ticket 04 required the setup-index read (`HomeAssistantScenarios.ReadsTheSetupIndex`) in home
  and music scenarios and raised their ceilings by one; watch ceilings went up by two (load +
  index). Every home run now opens with one index read.
- Eval mechanics: a full pass takes ~5 minutes; launch detached (`setsid nohup … > log; echo exit`)
  and wait on the log; never `pkill -f "dotnet test"` from a shell whose own command line matches;
  never rebuild while a run is in flight; `scorecard-full.json` is overwritten by every armed run,
  copy it first.
- Left as is from review: `HomeAssistantSetupSummary` still lives under `Domain/Prompts` though
  it now renders a file; `HaFileSystem.DescribeMount` gained one sentence about the index.
