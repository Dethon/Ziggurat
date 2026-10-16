# 01 — A period started and ended from the Cycle tab

**What to build:** A fifth tab, Cycle, where she taps "Period started" (today or another day) and
later "Period ended". The status card shows the day of the period while it is ongoing and the day of
her cycle once it has ended; her past periods are listed, newest first, with their length and their
cycle's length. This ticket lays down the period itself: the `Periods` table and the migration an
existing log upgrades by, `PeriodRules`, and the list, create and replace routes.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/period-tracker/spec.md`

- [x] A period created through the API is stored and listed back, newest start first; an ongoing one is ended by a replace.
- [x] Refusals, each with its sentence and nothing stored: no start, an end before the start, a day after tomorrow in the home's zone, longer than 31 days, overlapping another period, a period starting after an ongoing one.
- [x] Every period route sits behind the app's access gate.
- [x] The server starts against a database from before the table, gains it, and keeps its meals and doses.
- [x] Cycle day, period day and cycle lengths are pure functions under vitest.
- [x] Every new string is in English and Spanish, and the tab's icon is inline SVG.
