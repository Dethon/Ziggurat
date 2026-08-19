# 04 — Judged checks in the full tier

Status: ready-for-agent

Claims that are judgments (`subagents.success-criteria-are-stated`, `subagents.answer-is-synthesised`,
`timers.id-is-descriptive`) wait on "the judge pass" and never run. Instead of a new tier:
`Scenario` gains `Judged` — claim id plus rubric — evaluated in `EvalSuite.RunAsync` beside the
deterministic checks, against the same recording. A verdict failure fails the run like any other
check and lands in the dump with the judge's reason. Judge model pinned in the repo, verdict is
strict JSON, one retry then a loud failure. The scorecard marks those claims `coverage: judged`.
Prompt building and verdict parsing are unit-tested against a fake transport.
