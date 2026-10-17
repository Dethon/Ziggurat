# Sugary: her exercise beside her meals

Status: ready-for-agent

## Problem Statement

Exercise moves her glucose as surely as a meal or a dose does: a walk after lunch can bring a high
down, a session at the gym can push it up first and down hours later. Sugary keeps her meals, their
outcomes, her doses and her cycle, but not when she moved. A meal that ended low for no reason the
log shows, a night that ran low after the gym, cannot be explained by anyone reading `/sugary` beside
`/ha` without asking her what she did that day, and she will not remember.

## Solution

Sugary's Log records her exercise as it happens. Two shortcuts sit at the top of the Log, **Paseo**
(Walk) and **Gimnasio** (Gym), because those are what she does: one tap starts one, the Log shows it
running with its minutes, and one tap stops it. Anything else is started from **Otro** (Other) with a
name she gives it. A session she forgot to start goes in afterwards with its time and its minutes.
Each session is a row in the day's timeline beside her meals and doses, and a table at `/sugary`
that Jonas reads.

- **Tap to start, tap to stop.** Tapping Paseo or Gimnasio starts it at once, with nothing to fill
  in; the shortcuts make way for a running card with the minutes so far and Stop. Stopping saves it.
  Closing the app or picking up another device does not lose it: a running session is kept by the
  server.
- **Another kind, by name.** Otro asks for a name (bici, natación…), offering the names she has used
  before, then starts it as the shortcuts do.
- **A session afterwards.** A session she did not start on time goes in with its kind, when it
  started and how many minutes it lasted.
- **In the day's timeline.** A session shows in the Log among the meals and doses, in time order, with
  its kind, its time and its minutes. Opened, it can be changed or deleted, with undo from the toast.
- **A forgotten stop is caught.** A session runs at most twelve hours; one left running past that is
  stopped by saying when it ended, never by saving twelve hours she did not do.
- **Tables like the rest of her log.** `/sugary` renders her sessions as `exercise/<YYYY-MM>.csv`,
  the README explains them, and the settings offer them as a download.
- **Jonas can read it.** The `sugary-log` skill says where her exercise is, so "did she exercise the
  day her glucose dropped" is a read beside `/ha`, and says it is her record, never a basis for
  suggesting carbs or insulin around exercise.

## User Stories

### Recording a session

1. As the person counting carbs, I want to start a walk with one tap, so that recording it costs
   nothing as I leave the house.
2. As the person counting carbs, I want to start a gym session with one tap, so that the two things I
   do most need no typing.
3. As the person counting carbs, I want to see a session that is running with how long it has been
   going, so that I know it is being recorded.
4. As the person counting carbs, I want to stop a running session with one tap, so that its length is
   recorded without arithmetic.
5. As the person counting carbs, I want a running session to survive closing the app or switching
   phones, so that I never lose a walk because the screen went off.
6. As the person counting carbs, I want to start another kind of exercise by giving it a name, with
   the names I used before offered again, so that a bike ride or a swim is two taps the second time.
7. As the person counting carbs, I want to add a session I forgot to start, with when it started and
   how many minutes it lasted, so that it is still in my log.
8. As the person counting carbs, I want a session I left running for hours to ask when it really
   ended instead of saving the hours I did not exercise, so that my log stays one I can trust.
9. As the person counting carbs, I want a session whose times make no sense (an end before its start,
   a start in the future, longer than twelve hours, a second one while one is running) refused with a
   sentence that says why, so that nothing wrong is kept.

### Seeing and changing it

10. As the person counting carbs, I want each session in the Log's day among my meals and doses, in
    time order, with its kind, its time and its minutes, so that I read what I ate, injected and did
    together.
11. As the person counting carbs, I want to open a session to change its kind, its name, its start,
    its minutes or its note, so that a mistake can be put right.
12. As the person counting carbs, I want to delete a session with a moment to undo it, so that one
    started by mistake goes away and one deleted by mistake comes back.
13. As the person counting carbs, I want the exercise screens in the app's language, with the
    shortcuts reading Paseo and Gimnasio in Spanish and Walk and Gym in English, so that I read them
    without translating.

### The tables

14. As the person counting carbs, I want to download my exercise as a table in the settings beside
    the meals and doses, so that I can share it with my care team.
15. As another agent with the `/sugary` mount, I want her sessions as monthly tables with their start,
    end, minutes, kind and name in the home's clock, so that I can line them up with glucose under
    `/ha` without arithmetic.
16. As another agent, I want the README to say a running session has no end and no minutes yet, so
    that I never read one as zero minutes long.
17. As Jonas, I want the `sugary-log` skill to say where her exercise is and that it is never a basis
    for suggesting carbs or insulin, so that a question about exercise and glucose is a read and
    never advice.

### Operating it

18. As the operator, I want sessions stored in Sugary's own database beside the meals, gained by
    migration on start, so that there is one store and an existing log upgrades without losing
    anything.
19. As the operator, I want the `sugary` agent that estimates meals never to see her exercise, so
    that an estimate is never influenced by it.

## Implementation Decisions

### The session

- A **session** has an identifier, its **kind** (`walk`, `gym` or `other`), a **name** (required for
  `other` and at most 40 characters, absent for the other two), the instant it **started**, the
  instant it **ended** (absent while running) and an optional **note** (at most 200 characters).
- Kind is an enum in Domain beside `DoseKind`, stored and rendered as text as the other enums are,
  and spelled in English (`walk`, `gym`, `other`) in the API and the tables; the app shows the words
  in her language.
- Instants, not days: exercise lines up with glucose by the hour, as meals and doses do.
- **Minutes are never stored.** They are worked out from the start and the end on every read, so
  they can never disagree with them, as a period's `days` are. The app's "minutes" field sets the
  end from the start.
- **Rules**, refusing with a sentence and never adjusting what she sent (one place, `ExerciseRules`,
  beside `DoseRules` and `PeriodRules`):
  - a start is required, and is not after now, with five minutes of slack for a phone's clock;
  - an end, when given, is after the start and not after now with the same slack;
  - a session is at most 12 hours: long enough for a hike, short enough to catch a forgotten stop;
  - `other` needs a name; a name is at most 40 characters; a note at most 200;
  - **one running at a time.** Starting a session while another is running is refused with a
    sentence naming it ("A walk has been going since 10:05. Stop it first."). The app never offers
    it — while one runs the shortcuts are replaced by its card — so the refusal only catches a second
    phone. Refusing rather than stopping the running one keeps every end one she chose.
- A name is stored as she typed it, trimmed.

### Store

- An `ExerciseSessions` table in the Sugary server's SQLite database, the start stored as UTC text
  and indexed as `TakenAt` is, added by an EF migration the existing migrator applies on start.
- `ISugaryLog` gains `ExerciseTimesAsync` (every start, oldest first, to say which months have a
  file) and `ExerciseAsync(from, to)` (the sessions started in `[from, to)`, oldest first). Domain
  gains `ExerciseKind` and a `LoggedExercise` record beside `LoggedDose`.

### HTTP API

Behind the same access gate as every other app route.

- `GET /api/exercise` — newest start first, paged as doses are, optionally bounded by `from`/`to`;
  a running session is in it, with no end.
- `POST /api/exercise` — a session, running when it has no end. `PUT /api/exercise/{id}` replaces
  one, which is also how it is stopped (the end set). `DELETE /api/exercise/{id}`.
- `GET /api/exercise/names` — the names of her `other` sessions, each once, most recently used
  first, at most eight: the page of sessions the Log has loaded falls short once older ones scroll
  off.
- `GET /api/export/exercise.csv`, with the same period and clock parameters as the other downloads.

### Tables and the mount

- `exercise/<YYYY-MM>.csv`, a month present when a session started in it, filed by its start in the
  home's zone. Columns: `exercise_id`, `started_at`, `ended_at`, `minutes`, `kind`, `name`, `note`.
  Times carry the home's offset as the dose times do; `minutes` is the whole minutes between them,
  rounded; `ended_at` and `minutes` are empty while a session is running. Oldest first, through the
  same CSV writer and rules (formula escape included).
- Listing, glob, info, read and text search cover the months as they cover dose months. Nothing
  writes.
- The README gains an `exercise/<YYYY-MM>.csv` section: `walk`, `gym` and `other` with its name;
  minutes worked out from the two times; a running session's empty end; the times share the meals'
  clock; and that the table is her record of what she did, never a basis for suggesting carbs or
  insulin.

### The app

- **The Log's exercise row**, under Add dose: the chips **Paseo** / **Walk**, **Gimnasio** / **Gym**
  and **Otro** / **Other**, each with a drawn icon. Tapping Paseo or Gimnasio starts a session now
  with nothing more asked; the toast says it started and offers undo (the session deleted).
- **The running card** replaces the chips while a session runs: its kind or name, when it started,
  the minutes so far (ticking each minute), and **Stop**. Stop saves the end as now; the toast says it
  was saved with its minutes. Past twelve hours, Stop opens the session's sheet asking when it ended.
  The card opens the sheet too.
- **Otro** opens a sheet with the name (the names used before as chips) and Start.
- **Add a past session**, a quiet link under the chips, opens the sheet with the kind, the start
  (now less an hour by default, editable), the minutes (empty, never prefilled) and a note.
- **The session sheet** (a row or the running card tapped): kind, name for `other`, start, minutes
  (or "still going" for a running one), note, Save and Delete. Delete offers undo from the toast,
  saved again with every value she recorded.
- **Timeline.** `Entry` gains a third kind; a session sits in the day by its start, drawn with its
  kind's icon, its name, its time range and its minutes (a running one "in progress"). During a food
  search sessions step aside as doses do. The day's carb total counts meals only. The Log's empty
  state counts sessions.
- **Settings.** The downloads gain exercise.
- Every new string is in English and Spanish. Every icon is inline SVG in the icon component. New
  colours are tokens in both schemes, meeting AA where they carry text.

### The agents

- The `sugary` estimating agent is unchanged and never sees her exercise.
- The `sugary-log` skill names `/sugary/exercise/<YYYY-MM>.csv` and says it is her record, never a
  basis for suggesting carbs, a dose or a timing around exercise. Its description widens to her
  exercise. Its body budget is raised to fit, said in the manifest's comment; the skill edit gets a
  changed-tier eval run, per the repo's policy, once the user agrees to the spend.

## Testing Decisions

A good test drives behaviour through a boundary something real uses and asserts on what crosses it.

- **The Sugary server, booted**, through its HTTP API: starting a running session and listing it
  back, stopping it, creating a finished one, replacing and deleting one; each refusal (no start, a
  start in the future, an end before its start or in the future, over twelve hours, `other` with no
  name, a name or a note too long, an unknown kind, a second running session) with its sentence and
  nothing stored; the names list (`other` only, each once, most recent first, at most eight); the
  access gate on every new route; the download. Through MCP: the months, `ended_at` and `minutes`
  empty for a running session, minutes rounded, times in the home's zone, the formula escape on a
  note, text search, writes refused, the README's section. Prior art: `SugaryDoseApiTests`,
  `SugaryExportApiTests`, `McpSugaryServerTests`.
- **The migration**: the server starting against a database from before the new table, keeping its
  meals, doses and periods.
- **The mount in isolation**: the months listed and globbed beside the others, against the fake
  `ISugaryLog` of `SugaryFileSystemTests`; the conformance row passes unchanged.
- **The app's pure modules under vitest**: the timeline merging sessions with meals and doses; the
  running session's minutes; whether Stop can stop now or must ask; the minutes field read and the
  end it gives; the session restored for undo. Prior art: `log.test.ts`, `dose.test.ts`.
- **No E2E change.** The skill edit gets a changed-tier eval run.

## Out of Scope

- Intensity, heart rate, steps, distance, calories or any measurement of the session.
- Reading exercise from a watch, a phone's health app or Home Assistant. Every session is hers.
- Any suggestion around exercise — carbs before, a reduced dose, a correction after — ever, in the
  app, the server or any agent.
- Reminders, or a timer that ends a session by itself.
- Analysis in the app of exercise against glucose. The record is laid side by side.
- Giving the `sugary` agent any knowledge of her exercise.

## Further Notes

- The twelve-hour bound catches a forgotten stop, not a limit on what she can do. It refuses, it
  never clamps, and the app asks for the real end rather than trimming one.
- A session's start is when she tapped, so a walk started at the door is a few minutes early or
  late; a reader lining it up with glucose reads it to the quarter hour, not the minute.
