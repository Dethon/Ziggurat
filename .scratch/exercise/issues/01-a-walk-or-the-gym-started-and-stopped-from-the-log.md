# 01 — A walk or the gym started and stopped from the Log

**What to build:** The Log gains the exercise shortcuts, Paseo and Gimnasio (Walk and Gym in
English). One tap starts a session now; the shortcuts make way for a running card with the minutes so
far and Stop; Stop saves it. The session is kept in Sugary's database and shows in that day's
timeline among the meals and doses, with its kind, its time and its minutes. This ticket lays down
the session itself: the table, the migration, the shared rules, and the create, replace and list
routes.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/exercise/spec.md`

- [ ] A session created through the app's API with no end is listed back running; one replaced with an end is listed back finished. Listing is newest start first, paged, and bounded by `from`/`to`.
- [ ] Refusals, each with its sentence and nothing stored: no start, a start in the future past the slack, an end before its start or in the future, over twelve hours, an unknown kind, `other` with no name, a name over 40 or a note over 200 characters, a second running session (naming the one that is running).
- [ ] Every exercise route sits behind the app's access gate.
- [ ] The server starts against a database created before the exercise table, gains it by migration, and keeps its meals, doses and periods.
- [ ] The Log shows the Paseo / Gimnasio shortcuts; a tap starts a session with nothing asked, its toast offering undo.
- [ ] While a session runs the shortcuts are replaced by its card, its minutes ticking each minute (vitest on the minutes); Stop saves the end as now, and past twelve hours does not stop it now (vitest on that decision).
- [ ] The day timeline lists meals, doses and sessions together in time order (vitest on the merge); a session row shows its kind, its time range and its minutes, a running one in progress; the day's carb total is unchanged.
- [ ] Every new string is in English and Spanish, and any new icon is inline SVG.
