# sugary-log — description 95 / 100 tokens, body 739 / 750 tokens, served by mcp-sugary

================================================================================================

---
name: sugary-log
description: Reading her carb log under `/sugary` — the foods she keeps, the meals she logged and the foods in each, her insulin doses, her exercise and her periods ("when did she last have paella", "how did her glucose do after pizza", "how much insulin did she take for it", "was she on her period that week"). Which tables there are, where their columns are explained, and how to ask them.
---

### Her carb log at `/sugary`

Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

`/sugary/README.md` explains every column. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.

Her log is tables, asked with `domain__filesystem__query` on `/sugary`: one DuckDB SQL statement sees every month at once.

- `meals` — one row per meal she logged: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards (`outcome`).
- `items` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her. `meal_id` joins it to `meals`.
- `doses` — one row per insulin dose she took: its `units`, `rapid` or `basal` (long-acting). `dose_meals` links it to each meal it was for, maybe several.
- `exercise` — one row per session she started: `walk`, `gym` or a named `other`, its start and end, and its `minutes` (empty while it is running).
- `foods` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
- `periods` — her menstrual periods: `start_date`, `end_date` (empty while it is going), each one's length and its cycle's. `day_notes` — what she wrote about a day. Both are days on the home's calendar: read `/ha` history for those days beside them.

Every time is on the home's clock, the one the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up. Beside each is `<column>_local`, its wall time, for a day or an hour of her life: `eaten_at_local::date = '2026-10-03'`.

Her doses are her record of what she took, never a basis for advice. Asked what she should take, answer with what she took, the meal's logged carbs and what her glucose did, and give no number of units for any meal — not her ratio applied to its carbs, not even as arithmetic someone asked for — and no correction or timing, and no carbs or dose around exercise: her dose is hers and her diabetes team's to decide.

### Asking

- When she last ate something: `select eaten_at from items where name ilike '%lentejas%' order by eaten_at desc limit 1`.
- What she took for a meal: `doses` joined to `dose_meals` on `dose_id`, then to `meals` on `meal_id`. A day's units: `doses` alone, so a dose for two meals counts once.
- A total, an average or a count: one statement that computes it, never a table read whole to add up by hand.
- A table is not text, and reading one as a file is refused. For a chart, a longer analysis or her log beside `/ha` history, run a command in the sandbox: `duckdb` and Python's `polars` read `/sugary/meals/*.parquet` and the other tables' files directly.
