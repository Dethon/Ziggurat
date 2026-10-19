# Sugary: her insulin doses in the log

Status: ready-for-agent

## Problem Statement

She counts the carbs in a meal with Sugary so she can decide her insulin. The app keeps what she ate,
and after the meal she records whether her glucose ended in range, high or low. It does not keep
the one number in between: how much insulin she took.

That gap costs her two things:

- **The log cannot explain an outcome.** A meal marked "high" might have been under-counted, or the
  carbs might have been right and the dose short, late or forgotten. Without the dose, nobody
  reading the log can tell which, and that question is the reason the log exists.
- **Her doses live somewhere else, or nowhere.** Rapid doses at meals, corrections between meals
  and her daily basal dose are kept in her head, a pen's memory or a note. None of it can be put
  beside the glucose history the Home Assistant mount already serves, or beside her meals.

Glucose itself is not the problem. The sensor connected to Home Assistant records it, and nothing
here should record it again.

## Solution

Sugary keeps a **dose log** beside its meals: each dose is a time, a number of units and a kind,
rapid or basal. A dose can be linked to a meal or stand alone.

- **When she saves a meal**, she can add the rapid insulin she took for it. The dose is saved with
  the meal and linked to it.
- **From the Log tab**, she can add a dose at any time, rapid or basal, linked to no meal: a
  correction, her basal, or a meal dose she took before photographing the plate.
- **In the Log**, doses appear in the day's timeline beside the meals, and a meal shows the rapid
  insulin linked to it. Every dose can be edited and deleted.
- **The doses are tables like the rest of her log.** `/sugary` renders them as one CSV per month,
  the README explains the columns, and the app's settings offer them as a download. Jonas, who
  reads `/sugary` beside `/ha`, can line a meal up with its dose and the glucose after it.
- **Sugary only records doses.** Nothing in the app or in the server computes, prefills or suggests
  a dose. The `sugary` agent, which estimates meals, never sees them.

## User Stories

### Recording a dose with a meal

1. As the person counting carbs, I want to note the rapid insulin I took when I save a meal, so that
   the meal and its dose are recorded together.
2. As the person counting carbs, I want that field to be optional, so that a meal I log without a
   dose (a snack I did not dose for, or a dose I will add later) saves as it does today.
3. As the person counting carbs, I want the units field empty every time, never filled from the carbs
   or from my last dose, so that the number recorded is always one I entered.
4. As the person counting carbs, I want a dose noted with a meal to be stamped with the meal's time,
   so that I do not enter the time twice.
5. As the person counting carbs, I want a meal and its dose saved as one, so that a failure never
   leaves the meal saved and the dose silently lost.
6. As the person counting carbs, I want to enter fractional units (half units from a pen, smaller
   steps from a pump), so that the record matches what I injected.

### Recording a dose on its own

7. As the person counting carbs, I want to add a dose from the Log tab at any time, so that a
   correction or my basal is recorded without inventing a meal.
8. As the person counting carbs, I want to choose rapid or basal for a dose I add on its own, so that
   a reader can tell a meal bolus from my long-acting dose.
9. As the person counting carbs, I want a dose I add on its own to default to now, with the time
   editable, so that I can record one I took earlier.
10. As the person counting carbs, I want to add a short note to a dose, so that I can say why I took
    it ("correction", "pre-bolus for dinner").
11. As the person counting carbs, I want a dose I add on its own to be linkable to one of my recent
    meals, so that a dose taken before the meal was saved still belongs to it.

### Seeing and changing doses

12. As the person counting carbs, I want my doses shown in the Log's day timeline beside my meals, in
    time order, so that I see my day as it happened.
13. As the person counting carbs, I want each dose shown with its units, its kind and its time, so
    that I can read it at a glance.
14. As the person counting carbs, I want a meal in the Log to show the rapid insulin linked to it, so
    that carbs and dose are read together.
15. As the person counting carbs, I want rapid and basal doses told apart at a glance, so that I never
    misread one as the other.
16. As the person counting carbs, I want to edit a dose's units, kind, time, note and meal, so that a
    mistake can be put right.
17. As the person counting carbs, I want to delete a dose, with a moment to undo it, so that a dose
    entered by mistake goes away and one deleted by mistake comes back.
18. As the person counting carbs, I want deleting a meal to keep the doses linked to it, so that
    insulin I really took is never erased along with a meal I logged wrongly.
19. As the person counting carbs, I want a dose that is refused (no units, an impossible number) to
    say why, so that I can correct it.
20. As the person counting carbs, I want the dose screens in the language the app is set to, English
    or Spanish, so that I read them without translating.

### The tables

21. As the person counting carbs, I want to download my doses as a table in the app's settings, beside
    the meals, items and foods, so that I can share them with my care team.
22. As another agent with the `/sugary` mount, I want her doses as one CSV per month, so that I read a
    month without reading years.
23. As another agent, I want each dose row to carry the meal it belongs to, so that I can join it to
    the meal and its items.
24. As another agent, I want every dose time written with its offset in the home's zone, so that I can
    line doses up with the glucose history the Home Assistant mount serves.
25. As another agent, I want the README to explain the dose columns, units and kinds, so that I never
    read basal units as a meal dose.
26. As another agent, I want the doses to show what is in the database at the moment I read them, so
    that nothing I read is stale.
27. As another agent, I want any attempt to write under `/sugary/doses` refused, so that the only way
    to change her doses is through Sugary itself.
28. As another agent, I want a month with doses but no meals to still have its dose file, so that a
    month of basal alone is not invisible.
29. As Jonas, I want the `sugary-log` skill to say where the doses are and how to find a meal's dose,
    so that "how much did she take for the pizza" is one search.
30. As Jonas, I want the skill to say that the doses are her record and never a basis for advising a
    dose, ratio or correction, so that reading them never turns into dosing advice.

### Safety and operating it

31. As the person counting carbs, I want Sugary never to suggest, compute or prefill a dose anywhere,
    so that the app stays a record and my care team decides my insulin. (Jonas, outside Sugary, may
    since 2026-10-19 — see Out of Scope.)
32. As the operator, I want the `sugary` agent that estimates meals never to see a dose, so that an
    estimate is never influenced by, and never comments on, insulin.
33. As the operator, I want doses stored in Sugary's own database beside the meals, so that there is
    one store and one backup.
34. As the operator, I want the dose tables held to the same CSV rules as the others (RFC 4180, no
    byte-order mark, formula characters escaped), so that an export opened in a spreadsheet is safe.
35. As the operator, I want the database to gain the dose table by migration on start, so that an
    existing log upgrades without losing a meal.

## Implementation Decisions

### The dose

- A **dose** has an identifier, the instant it was taken, its **units** (insulin units, a positive
  number with at most two decimals and at most 100), its **kind** (`rapid` or `basal`), an optional
  **note** (at most 200 characters) and an optional **meal** it belongs to.
- Kind is an enum in Domain beside `ItemSource` and `MealOutcome`, stored and rendered as text,
  as the other enums are.
- A meal may have any number of doses (a split dose, a top-up). A dose belongs to at most one meal.
- The link is not a cascading foreign key. Deleting a meal clears the link on its doses and keeps
  them, the same reasoning as an item's `FoodId` outliving its food: a dose is a fact of what she
  injected, and it outlives the record of the meal.
- Validation lives in one place beside `MealRules`, so the HTTP API and anything else that writes a
  dose refuse with the same sentence.

### Store

- A new **Doses** table in the Sugary server's SQLite database (ADR-0043), added by an EF migration
  that the existing migrator applies on start. The instant is stored as UTC text and indexed, as
  meals' `EatenAt` is. The meal link is indexed.
- `ISugaryLog` gains reads for doses alongside meals: the instants of every dose (to say which
  months have a file) and the doses taken in a range, oldest first, each with its meal identifier.
  Domain gains a `LoggedDose` record beside `LoggedMeal`.

### HTTP API

Behind the same access gate as every other app route.

- `GET /api/doses` — newest first, paged like meals, optionally bounded by `from`/`to` so the Log
  can fetch the days it shows.
- `POST /api/doses`, `PUT /api/doses/{id}`, `DELETE /api/doses/{id}` — create, replace, delete. A
  dose naming a meal that does not exist is refused.
- **Saving a meal may carry one dose.** The meal create request gains an optional dose (units, and
  optionally a note), always `rapid`, stamped with the meal's `eatenAt`, created in the same
  transaction as the meal and linked to it. A refused dose refuses the whole save. A meal update
  (the whole meal sent again) does not touch its doses; doses are changed through their own routes.
- The meal list answers each meal with the identifiers and units of its linked doses, so the Log
  shows a meal's insulin without a second lookup per meal.
- `GET /api/export/doses.csv`, with the same period and clock parameters as the other downloads.

### Tables and the mount

- `SugaryTables` renders doses with the columns `dose_id`, `taken_at`, `kind`, `units`, `meal_id`,
  `note`, through the same CSV writer and rules as the other tables.
- The meals and items tables are unchanged. A reader joins on `meal_id`. This also keeps the
  standalone import, which reads the meals and items columns by name, untouched.
- The `/sugary` mount gains `doses/<YYYY-MM>.csv`, a month present when it holds at least one dose,
  independently of meals. Listing, glob, info, read and text search cover it like the meal months.
  Nothing writes.
- The README gains a `doses/<YYYY-MM>.csv` section: units are insulin units; `rapid` is
  rapid-acting (meal and correction doses), `basal` is long-acting; `meal_id` is empty for a dose
  linked to no meal; the times share the meals' clock. It says the table is her record of what she
  took, never a recommendation.
- The `sugary-log` skill names the doses table, how to find a meal's doses (text search for its
  `meal_id` under `/sugary/doses`), and that the doses are a record to read, never a basis for
  suggesting a dose, ratio, correction or timing. Its description is widened to cover questions
  about her insulin. Per the repo's eval policy, a skill edit is followed by a changed-tier run.

### The `sugary` agent

- Unchanged. It has no tools and no mount, and a dose never travels on an estimate turn. The
  estimating prompt's rule against suggesting doses stays as written.

### The app

- **Meal screen.** The save area gains an optional "Insulin taken" units field (rapid). It starts
  empty on every new meal and after a reset, and is never prefilled. A value is sent with the meal
  create request.
- **Log tab.** A button adds a dose on its own: units (empty), kind (rapid by default), time (now,
  editable), note, and an optional meal chosen from the meals of the last day or so.
- **Timeline.** Each day lists meals and doses together in time order. A dose row shows its units,
  its kind and its time, with rapid and basal drawn differently. A meal row shows the total rapid
  units linked to it.
- **Dose sheet.** Tapping a dose opens a sheet to edit or delete it. Deleting offers undo through
  the existing toast, as recording an outcome does.
- **Deleting a meal** with linked doses says the doses are kept.
- **Settings.** The downloads gain doses.
- Every new string is in English and Spanish. Any new icon is drawn as inline SVG in the icon
  component, never an emoji or a typed glyph.
- The units input accepts a comma or a point as the decimal separator, as the portion inputs do.

## Testing Decisions

A good test drives behaviour through a boundary something real uses and asserts on what crosses it,
never on how the server or the app arranges itself inside.

- **The Sugary server, booted (most of the behaviour).** Through its HTTP API as the app calls it:
  - creating, listing (paged and by period), replacing and deleting a dose; each refusal (no units,
    zero, negative, over 100, more than two decimals, an unknown kind, a note too long, an unknown
    meal) with its sentence and nothing stored;
  - a meal saved with a dose: both stored, the dose `rapid`, stamped with the meal's time and
    linked; a meal saved with a refused dose stores neither;
  - a meal update leaving its doses as they were; the meal list carrying each meal's dose units;
  - deleting a meal keeping its doses with the link cleared;
  - the access gate on every dose route;
  - the doses download, with its period and clock.

  Through MCP, as Jonas reads it: `doses/<YYYY-MM>.csv` for a month with doses and none for a month
  without, a month with doses and no meals, times in the home's zone, the formula escape on a note,
  text search finding a meal's doses by `meal_id`, writes refused, and the README's dose section.

  Prior art: `SugaryMealApiTests`, `SugaryExportApiTests`, `McpSugaryServerTests`, and the
  `SugaryServer` fixture.
- **The mount's rendering in isolation**, where the server test would be slow to vary: the dose
  months listed and globbed beside meal months, against a fake `ISugaryLog`. Prior art:
  `SugaryFileSystemTests`, and the filesystem conformance row, which must still pass unchanged.
- **The migration.** The server starting against a database created before the dose table, with its
  meals intact afterwards. Prior art: the import tests' database fixture.
- **The app's pure modules under vitest.** The day timeline merging meals and doses in time order;
  a meal's linked rapid units; the dose form's parsing and validation (comma decimals, the bounds);
  the meal draft's dose field starting empty and cleared on reset. Prior art: `log.test.ts`,
  `meal.test.ts`, `draft.test.ts`.
- **No E2E change and no new eval scenario.** The skill edit gets a changed-tier eval run, as any
  skill edit does.

## Out of Scope

- Glucose: reading the sensor, recording readings, or filling a meal's outcome from them. The sensor
  connected to Home Assistant is the record of glucose.
- Any dose suggestion, calculation, ratio, correction factor, insulin-on-board figure or prefill in
  the app, the server or the `sugary` agent that estimates meals. (Narrowed 2026-10-19: this used to
  say "or any agent". Jonas may now work a dose out — from the ratio, correction factor, target and
  insulin duration she or fran gave him, with the logged carb range, insulin still on board and how
  similar meals went — asking for any value he lacks and inventing none. See the `sugary-log` skill.)
- Reading doses from a pump, a smart pen or Home Assistant. Every dose is entered by her.
- Insulin brands or types beyond rapid and basal, and mixed insulins.
- Reminders or alerts about doses.
- Changing the meals or items tables' columns, or the standalone import.
- Giving the `sugary` agent the mount or any knowledge of doses.

## Further Notes

- Doses carry the same health weight as the carb figures. The bounds on units, keeping doses when a
  meal is deleted, and the refusal to prefill are safety properties, each with a test.
- The upper bound of 100 units catches a slipped digit, not a clinical limit. It refuses, it never
  clamps.
- A meal dose is stamped with the meal's time because the app has no better one. If she takes it
  earlier (a pre-bolus), she can change the time from the Log. The README says that a dose saved with
  a meal is stamped with the meal's time unless she changed it, so a reader does not take a
  zero-minute gap between dose and meal as measured.
