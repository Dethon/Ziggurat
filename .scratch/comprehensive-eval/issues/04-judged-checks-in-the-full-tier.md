# 04 — Judged checks in the full tier

Status: resolved

Claims that are judgments (`subagents.success-criteria-are-stated`, `subagents.answer-is-synthesised`,
`timers.id-is-descriptive`) wait on "the judge pass" and never run. Instead of a new tier:
`Scenario` gains `Judged` — claim id plus rubric — evaluated in `EvalSuite.RunAsync` beside the
deterministic checks, against the same recording. A verdict failure fails the run like any other
check and lands in the dump with the judge's reason. Judge model pinned in the repo, verdict is
strict JSON, one retry then a loud failure. The scorecard marks those claims `coverage: judged`.
Prompt building and verdict parsing are unit-tested against a fake transport.

## Answer

Done. `JudgedCheck(claim, rubric)` on `Scenario`, graded by `Judge` — pinned to
`anthropic/claude-sonnet-5` (verified live on OpenRouter; a different vendor than the model
under test), temperature 0, strict-JSON verdict parsed out of whatever prose wraps it, one
retry then a loud "could not answer" failure that never counts as a pass. Runs inside the
k-of-N policy, only on runs the deterministic checks already passed. Three checks attached:
the timer id on the rubbish reminder, steps-not-narrated on the booking reply, and
edits-are-surgical on the vault edit's own diff. The two delegation judgements stay exempt —
their blocker is a scenario that reliably delegates, which is the standing finding.
Deterministic halves pinned in JudgeTests; the verdicts themselves validate on the armed run.
