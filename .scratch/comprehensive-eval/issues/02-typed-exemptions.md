# 02 — Typed exemptions

Status: resolved

`ClaimExemptions.Reasons` is prose only; the backlog cannot be counted. Each entry becomes a
typed record — `guard` (a scenario runs, it just cannot earn the citation), `needs-fixture`,
`unfalsifiable` (as written), `judge` (a judgment, waiting on issue 04) — and the scorecard's
claim rows gain a `coverage` field: `cited`, `judged`, or the exemption kind. Coverage tests
keep their semantics; the scorecard can then say how much of the prompt surface is under test.

## Answer

Done. Six kinds — guard, unwritten, needs-fixture, unfalsifiable, judge, finding — with the
kind's meaning documented on the enum. Every entry classified; the stale "the eval hosts no
search" reason on heavy-work-is-delegated updated while passing. The scorecard's claim rows
carry `coverage` (cited or the exemption kind), built in `EvalScorecard.Coverage` from the same
two sources the coverage tests read.
