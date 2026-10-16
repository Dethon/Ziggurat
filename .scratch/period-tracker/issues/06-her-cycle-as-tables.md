# 06 — Her cycle as tables

**What to build:** `/sugary/periods.csv` (`period_id`, `start_date`, `end_date`, `days`,
`cycle_days`) and `/sugary/day-notes.csv` (`date`, `note`), oldest first, rendered from the store
on every read; the README's sections on both, saying the dates are home-calendar days, an ongoing
period has no end, and the forecasts are not in the tables; and both as downloads in settings.

**Blocked by:** 03

**Status:** ready-for-agent

Spec: `.scratch/period-tracker/spec.md`

- [ ] Through MCP: both files, the derived columns, the formula escape, text search, writes refused, the README's sections.
- [ ] The mount in isolation lists and globs both files; the conformance row passes unchanged.
- [ ] Both downloads, behind the access gate.
