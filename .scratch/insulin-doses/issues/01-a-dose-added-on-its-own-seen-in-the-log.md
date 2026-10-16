# 01 — A dose added on its own, seen in the Log

**What to build:** From the Log tab she adds a dose: its units, rapid or basal, when she took it (now
by default, editable) and an optional note. The dose is kept in Sugary's database and shows up in
that day's timeline beside her meals, in time order, with its units, kind and time. Rapid and basal
look different. A dose that is refused says why. This ticket lays down the dose itself: the table
(with its optional meal link, unused until ticket 03), the migration an existing log upgrades by,
the shared validation, and the create and list routes.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [ ] A dose created through the app's API is stored and listed back, newest first, paged, and bounded by `from`/`to`.
- [ ] Refusals, each with its sentence and nothing stored: no units, zero, negative, over 100, more than two decimals, an unknown kind, a note over 200 characters, an unknown meal.
- [ ] Every dose route sits behind the app's access gate.
- [ ] The server starts against a database created before the dose table, gains the table by migration, and keeps its meals.
- [ ] The Log tab's add-dose sheet starts with the units empty (never prefilled), rapid selected and the time set to now. It accepts a comma or a point as the decimal separator.
- [ ] The day timeline lists meals and doses together in time order (vitest on the merge), and a dose row shows units, kind and time.
- [ ] Every new string is in English and Spanish, and any new icon is inline SVG.
