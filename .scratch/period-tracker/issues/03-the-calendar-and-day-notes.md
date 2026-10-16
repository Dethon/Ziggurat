# 03 — The calendar and a note on any day

**What to build:** A month calendar on the Cycle tab, Monday first, with her period days filled
(an ongoing one up to today), the days with a note dotted and today ringed, and previous and next
month buttons. Tapping a day opens a sheet with its note and the period action that applies: start a
period on it, end the ongoing one on it, or open the period it belongs to. Notes live in a
`DayNotes` table keyed by day, added by migration.

**Blocked by:** 02

**Status:** ready-for-agent

Spec: `.scratch/period-tracker/spec.md`

- [ ] A note set, replaced and cleared (empty text deletes it) through the API; listed by period.
- [ ] Refusals: over 500 characters, a day after tomorrow, a day that is not a date.
- [ ] Every day-note route sits behind the access gate.
- [ ] The calendar grid (Monday first, each day's marks) is a pure function under vitest.
- [ ] Each day button is named for screen readers with its date and what it is.
