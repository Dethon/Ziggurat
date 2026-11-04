---
name: sugary-log
description: Reading the asking person's own carb log under `/sugary` — the foods they keep, the meals they logged and the foods in each, their insulin doses, exercise and periods ("when did I last have paella", "how did my glucose do after pizza", "how much insulin did I take for it", "was I on my period that week"). Which tables there are, where their columns are explained, and how to ask them.
description-budget: 100
# Raised from 500 for insulin doses: where they are and how a meal's are found. Raised again to 700
# for periods and day notes, the tables a cycle question reads beside /ha, and to 750 for exercise.
# Raised to 850 when a dose stopped being refused and started being worked out (2026-10-19): only
# from numbers the person gave, missing ones asked for, the working shown - the rule here that guards
# their safety, and not one to cut thin. The log is the asking person's own (2026-11-03): written about
# one "her" and naming the home's people, it had a member of another household told about "her" log.
# Raised to 920 when a parent asked about their son's day was told the parent's own empty log was the
# son's (2026-11-04: 4/6 live, 0/3 in the eval before the paragraph saying it is nobody else's).
body-budget: 920
served-by: mcp-sugary
claims:
# The trigger claim: a request of this kind loads the skill.
- sugary-log.loads-for-a-question-about-her-log: A question about the foods a person keeps or the meals they logged loads the sugary-log skill before /sugary is queried.
# The body claims: a dose is worked out from the numbers the person gave, shown with its working, and
# a number they have not given is asked for rather than assumed.
- sugary-log.works-a-dose-from-her-numbers: Asked what dose to take with the person's ratio given, the reply works one out from that ratio over the meal's logged carb range, weighs how similar meals went after what they took, says which values it used, and invents no value it was not given.
- sugary-log.asks-for-a-missing-number: Asked what dose to take without the person's ratio, the reply asks for it and gives no number of units.
# The log is the asker's alone: asked about somebody else's - a son's who counts carbs too - the reply
# says it cannot see theirs, and never reports the asker's rows, or their absence, as that person's.
- sugary-log.another-persons-log-is-not-here: Asked about another person's meals or doses, the reply says only the asker's own log can be read and reports nothing from it as that person's.
---

### The asking person's carb log at `/sugary`

Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: it changes only through the Sugary app.

It is the asking person's log and nobody else's. Another person's — a child's, a partner's, however they log — is not here and cannot be read: asked about theirs, say you can see only the asker's own, and never give what this log holds, or that it is empty, as theirs.

`/sugary/README.md` explains every column. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.

The log is tables, asked with `domain__filesystem__query` on `/sugary`: one DuckDB SQL statement sees every month at once.

- `meals` — one row per meal they logged: its carbs as finally logged, the model's first estimate, and what their glucose did afterwards (`outcome`).
- `items` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by them. `meal_id` joins it to `meals`.
- `doses` — one row per insulin dose they took: its `units`, `rapid` or `basal` (long-acting). `dose_meals` links it to meals; unlinked, a rapid dose may be for a nearby meal, not only a correction.
- `exercise` — one row per session they started: `walk`, `gym` or a named `other`, its start and end, and its `minutes` (empty while it is running).
- `foods` — the foods they keep, one row each: what they call it, its carbs per 100 g or ml, the amount they usually have and, for a food kept only as a portion, the carbs in that portion.
- `periods` — their menstrual periods: `start_date`, `end_date` (empty while it is going), each one's length and its cycle's. `day_notes` — what they wrote about a day. Both are days on the home's calendar: read `/ha` history for those days beside them.

Every time is on the home's clock, the one the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up. Beside each is `<column>_local`, its wall time, for a day or an hour of their life: `eaten_at_local::date = '2026-10-03'`.

Asked what they should take, work a dose out from their own numbers, never a guessed one: their carb ratio; for a correction, their correction factor, target and glucose now (the sensor under `/ha`); and how long their rapid insulin acts. Use the values they gave you, which your memory keeps; ask for any you lack, and give no number until you have it. Show the working: the carbs as their logged range, so the dose is a range; rapid units from recent `doses` still on board, subtracted; any correction; and how similar meals went after what they took, naming them, where that shifts it. Glucose now and insulin on board are for a dose now, not one for a later meal. Adjust for exercise only by a rule they gave. Say which values you used, and that it is theirs to check before injecting.

### Asking

- When they last ate something: `select eaten_at from items where name ilike '%lentejas%' order by eaten_at desc limit 1`.
- What they took for a meal: `doses` joined to `dose_meals` on `dose_id`, then to `meals` on `meal_id`. A day's units: `doses` alone, so a dose for two meals counts once.
- A total, an average or a count: one statement that computes it, never a table read whole to add up by hand.
- A table is not text, and reading one as a file is refused. For a chart, a longer analysis or the log beside `/ha` history, run a command in the sandbox: `duckdb` and Python's `polars` read `/sugary/meals/*.parquet` and the other tables' files directly.
