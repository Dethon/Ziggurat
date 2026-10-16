# Sugary: her period beside her meals

Status: ready-for-agent

## Problem Statement

She suspects her menstrual cycle has something to do with how she eats and how her glucose behaves:
days when she wants more carbs, days when the same meal and the same dose end higher. Sugary keeps
her meals, their outcomes and her doses, and the Home Assistant mount keeps her glucose, but nothing
keeps her cycle. Without it nobody — not she, not her diabetes team, not Jonas reading `/sugary`
beside `/ha` — can put a week of high readings or a run of cravings next to where she was in her
cycle, and that comparison is the whole reason for wanting it.

## Solution

Sugary gains a **Cycle** tab. She records when a period started and when it ended, and can write a
note on any day. From her past periods the app shows where she is in her cycle, when the next period
is expected and her likely fertile window, always said to be an estimate.

- **Periods are a start and an end.** She taps "Period started" on the day it starts and "Period
  ended" on the day it ends; either day can be set to an earlier date. A period with no end yet is
  ongoing. Each can be edited and deleted.
- **A note on any day.** Any day of the calendar can carry a short note — how she felt, what she
  craved — whether or not it is a period day.
- **A month calendar** shows her period days, the expected next period, the fertile window, the
  days with a note and today.
- **A forecast from her own cycles.** Once she has two periods, the app estimates the next start
  and the fertile window from the lengths of her recent cycles. Every forecast says it is an
  estimate and not reliable for avoiding pregnancy.
- **The Log shows her period.** A day in the Log that was a period day says so in its header, so
  meals and cycle read together.
- **Tables like the rest of her log.** `/sugary` renders her periods and day notes as two CSVs, the
  README explains them, and the settings offer both as downloads. The forecasts are not in the
  tables: what is there is only what she recorded.
- **Jonas can read it.** The `sugary-log` skill says where her cycle is, so "was she on her period
  the week her glucose ran high" is a read of one file beside `/ha`.

## User Stories

### Recording a period

1. As the person tracking her cycle, I want to mark the day my period starts with one tap, so that
   recording it costs nothing.
2. As the person tracking her cycle, I want to mark a start on an earlier day, so that a period I
   forgot to record on the day still goes in on the right date.
3. As the person tracking her cycle, I want to mark the day my period ends, defaulting to today and
   editable, so that its length is recorded.
4. As the person tracking her cycle, I want a period I have not ended to show as ongoing, with the
   day of the period it has reached, so that a forgotten end is visible and easy to put right.
5. As the person tracking her cycle, I want a period whose dates make no sense (an end before its
   start, a day in the future, two periods overlapping, one longer than a month) refused with a
   sentence that says why, so that my record stays one I can trust.
6. As the person tracking her cycle, I want to change a period's start or end, or make an ended
   period ongoing again, so that a mistake can be put right.
7. As the person tracking her cycle, I want to delete a period, with a moment to undo it, so that one
   entered by mistake goes away and one deleted by mistake comes back.

### Notes on a day

8. As the person tracking her cycle, I want to write a short note on any day, period or not, so that
   I can say how I felt or what I craved.
9. As the person tracking her cycle, I want to change or clear a day's note, so that it says what I
   mean.

### Seeing my cycle

10. As the person tracking her cycle, I want a Cycle tab with my status at the top — the day of my
    period, or the day of my cycle — so that I know where I am at a glance.
11. As the person tracking her cycle, I want a month calendar with my period days, the days I wrote a
    note on and today marked, and the months before and after a swipe or tap away, so that I see my
    cycle as it went.
12. As the person tracking her cycle, I want to tap a day on the calendar to see its note, write one,
    or start or end a period on it, so that everything about a day is in one place.
13. As the person tracking her cycle, I want a list of my past periods with each one's length and the
    length of the cycle it began, so that I can see how regular I am.
14. As the person tracking her cycle, I want the Cycle screens in the language the app is set to,
    English or Spanish, so that I read them without translating.

### The forecast

15. As the person tracking her cycle, I want to see when my next period is expected, as a range of
    days from my recent cycles, so that I can plan around it.
16. As the person tracking her cycle, I want to see my likely fertile window, so that I know roughly
    where in my cycle I am.
17. As the person tracking her cycle, I want the expected period and fertile window drawn on the
    calendar differently from the days I recorded, so that I never mistake a forecast for a record.
18. As the person tracking her cycle, I want every forecast to say it is an estimate from my past
    cycles and not reliable for avoiding pregnancy, so that nobody relies on it for that.
19. As the person tracking her cycle, I want no forecast until I have recorded two periods, and an
    unusually long cycle (a period I forgot to record) left out of it, so that it is not built on
    too little or on a gap.

### The Log

20. As the person counting carbs, I want a day in the Log that was a period day to say so in its
    header, so that I read my meals beside my cycle.

### The tables

21. As the person tracking her cycle, I want to download my periods and day notes as tables in the
    settings, beside the meals and doses, so that I can share them with my care team.
22. As another agent with the `/sugary` mount, I want her periods as a table with their start, end,
    length and cycle length, so that I can line them up with meals and glucose without date
    arithmetic.
23. As another agent, I want her day notes as a table of dates and notes, so that I can see what she
    wrote around a period.
24. As another agent, I want the README to say the dates are days on the home's calendar and that an
    ongoing period has no end, so that I never read an open period as one day long.
25. As another agent, I want the README to say the app's forecasts are not in the tables, so that I
    never take a predicted day for one she recorded.
26. As Jonas, I want the `sugary-log` skill to say where her periods and day notes are, so that a
    question about her cycle and her glucose is one read beside `/ha`.

### Operating it

27. As the operator, I want periods and day notes stored in Sugary's own database beside the meals,
    so that there is one store and one backup.
28. As the operator, I want the database to gain the new tables by migration on start, so that an
    existing log upgrades without losing a meal.
29. As the operator, I want the `sugary` agent that estimates meals never to see her cycle, so that
    an estimate is never influenced by it.

## Implementation Decisions

### The period

- A **period** has an identifier, a **start** day and an optional **end** day. Both are calendar
  days (`yyyy-MM-dd`), not instants: a period is a day on her calendar, and an instant would put it
  on the wrong day for anyone reading it in another zone. No end means ongoing.
- **Rules**, refusing with a sentence and never adjusting what she sent (one place, `PeriodRules`,
  beside `DoseRules`):
  - a start is required;
  - the end is not before the start;
  - neither day is after today in the home's zone, with one day of slack for a phone in another
    zone;
  - a period is at most 31 days, start and end included — long enough for any period, short enough
    to catch a slipped month;
  - no two periods share a day. An ongoing period runs to the end of time for this rule, so only the
    latest period can be ongoing and a later one cannot start until it has ended.
- No upper bound on how far back a start may be.

### The day note

- A **day note** is a calendar day and its text, at most 500 characters. One note per day; writing an
  empty note deletes it. Any day not after today (with the same slack) can carry one.

### Store

- Two tables in the Sugary server's SQLite database, `Periods` (start indexed) and `DayNotes`
  (keyed by its day), each added by an EF migration the existing migrator applies on start. Days are
  stored as `yyyy-MM-dd` text, which sorts as days do.
- `ISugaryLog` gains `PeriodsAsync` and `DayNotesAsync`, each the whole list oldest first — they are
  small, as foods are. Domain gains `LoggedPeriod` and `LoggedDayNote` records beside `LoggedDose`.

### HTTP API

Behind the same access gate as every other app route.

- `GET /api/periods` — every period, newest start first.
- `POST /api/periods`, `PUT /api/periods/{id}`, `DELETE /api/periods/{id}`.
- `GET /api/day-notes` — every note, optionally bounded by `from`/`to` days.
- `PUT /api/day-notes/{day}` — set a day's note; empty text deletes it. `DELETE /api/day-notes/{day}`.
- `GET /api/export/periods.csv` and `GET /api/export/day-notes.csv`. Days have no clock, so neither
  takes a zone.

### Tables and the mount

- `periods.csv`: `period_id`, `start_date`, `end_date`, `days`, `cycle_days`. `days` is the
  period's length with both ends counted, empty while it is ongoing. `cycle_days` is the days from
  this start to the next one, empty on the latest period. Both are computed from her dates on every
  read, so they can never disagree with them. Oldest first.
- `day-notes.csv`: `date`, `note`. Oldest first. Through the same CSV writer and rules (formula
  escape included).
- Both are single files at the mount's root, as `foods.csv` is; listing, glob, info, read and text
  search cover them. Nothing writes.
- The README gains a section for each: the dates are days on the home's calendar, with no time and
  no offset; an ongoing period has an empty `end_date`; the app's forecasts are not stored and are
  not in the tables.

### The forecast (app only)

- A pure module computes it from her periods and today; nothing is stored or sent.
- **Cycle length** is the days from one start to the next. The forecast uses up to the six most
  recent cycles, leaving out any longer than 1.5 times their median (a period she did not record
  makes one cycle about twice as long).
- **Next period**: expected from the latest start plus the shortest to the longest kept cycle, with
  the latest start plus their mean (rounded) as its likeliest day.
- **Fertile window**: ovulation is taken as 14 days before the next start, the window the five days
  before it to the day after; with a range of next starts, from 19 days before the earliest to 13
  days before the latest. Shown while its end is not past.
- No forecast with fewer than two periods. The status says how many cycles it is based on.
- Each forecast carries the sentence that it is an estimate from her past cycles and not reliable for
  avoiding pregnancy.

### The app

- A fifth tab, **Cycle** / **Ciclo**, with a drawn icon.
- **Status card**: during a period, "Period · day N" with "Period ended" (today, or another day);
  otherwise "Cycle day N" with "Period started" (today, or another day); with no periods, a line on
  what the tab is for and "Period started".
- **Forecast card**: the expected next period as a range, the fertile window, the number of cycles
  it is based on and the estimate sentence.
- **Calendar**: a month grid starting on Monday, as both locales do, with previous and next month
  buttons. A recorded period day is filled; an ongoing period is filled up to today; an expected day
  and a fertile day are drawn with a dashed outline and a tint, distinct from recorded days; a day
  with a note has a dot; today has a ring. A legend says which is which. Each day is a button named
  for screen readers with its date and what it is.
- **Day sheet** (tapping a day): the day's note, and the period action that applies — start a
  period on it, end the ongoing period on it, or open the period it belongs to.
- **Period sheet**: start, end or ongoing, and delete, with undo from the toast.
- **Past periods**: newest first, each with its dates, its length and its cycle's length.
- **Log**: a day header carries a small mark, "Period", on each day inside a recorded period
  (ongoing ones up to today), keyed on the same phone-calendar day as the Log's days.
- **Settings**: the downloads gain periods and day notes.
- New colours are tokens in both schemes, meeting AA where they carry text; any icon is inline SVG
  in the icon component; every string is in English and Spanish.

### The agents

- The `sugary` estimating agent is unchanged and never sees her cycle.
- The `sugary-log` skill names `periods.csv` and `day-notes.csv` and widens its description to her
  cycle. Its body budget is raised to fit them, said in the manifest's comment; the skill edit gets a
  changed-tier eval run, per the repo's policy.

## Testing Decisions

A good test drives behaviour through a boundary something real uses and asserts on what crosses it.

- **The Sugary server, booted**, through its HTTP API: creating, listing, replacing and deleting a
  period; each refusal with its sentence and nothing stored; an ongoing period ended later; the
  overlap rule with an ongoing period; setting, replacing and clearing a day note and its refusals;
  the access gate on every new route; both downloads. Through MCP: both files, their derived
  columns, the formula escape on a note, text search, writes refused, the README's sections.
  Prior art: `SugaryDoseApiTests`, `SugaryExportApiTests`, `McpSugaryServerTests`.
- **The migration**: the server starting against a database from before the new tables, keeping its
  meals and doses.
- **The mount in isolation**: the new files listed and globbed beside the others, against the fake
  `ISugaryLog` of `SugaryFileSystemTests`; the conformance row passes unchanged.
- **The app's pure modules under vitest**: cycle day and period day; cycle lengths; the forecast
  (two periods needed, outlier left out, range and likeliest day, fertile window, an ongoing
  period); the calendar grid (Monday first, each day's marks); the Log's period days.
- **No E2E change.** The skill edit gets a changed-tier eval run.

## Out of Scope

- Flow intensity, symptoms as a list, moods, temperature, tests or any other daily measurement.
- Contraception or conception advice; the forecast is explicitly not for either.
- Notifications or reminders about an expected period.
- Reading the cycle from another app or from Home Assistant.
- Any analysis in the app of her cycle against her glucose or meals. The record is laid side by side;
  Jonas or her care team does the comparing.
- Giving the `sugary` agent any knowledge of her cycle.

## Further Notes

- A period is days, not instants, so its file has no offset; a reader lines a day up with the glucose
  history by reading `/ha` for that day on the home's calendar.
- The fertile-window rule is the usual calendar estimate (ovulation about 14 days before the next
  period). It is weakest exactly when cycles are irregular, which is why the sentence about
  pregnancy goes with it everywhere it is shown.
