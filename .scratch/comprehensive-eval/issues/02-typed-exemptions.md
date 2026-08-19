# 02 — Typed exemptions

Status: ready-for-agent

`ClaimExemptions.Reasons` is prose only; the backlog cannot be counted. Each entry becomes a
typed record — `guard` (a scenario runs, it just cannot earn the citation), `needs-fixture`,
`unfalsifiable` (as written), `judge` (a judgment, waiting on issue 04) — and the scorecard's
claim rows gain a `coverage` field: `cited`, `judged`, or the exemption kind. Coverage tests
keep their semantics; the scorecard can then say how much of the prompt surface is under test.
