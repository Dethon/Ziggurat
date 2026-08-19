# 01 — Per-scenario rates in the scorecard

Status: ready-for-agent

Guard scenarios run and assert but appear only as pass/fail tests: drift that stays above
threshold is invisible as a number. `EvalScorecard.Record` already receives the scenario and its
`ScenarioResult`; write a `scenarios` section beside `claims` — name → passes/runs/rate — seeded
from `EvalSuite.All` so a scenario that did not run shows `null` rather than being absent,
exactly as claims do. `Scorecard.Write` takes the new list; `ScorecardTests` pins the shape.
