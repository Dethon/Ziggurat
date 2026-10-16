# sugary-log — description 100 / 100 tokens, body 669 / 700 tokens, served by mcp-sugary

================================================================================================

---
name: sugary-log
description: Reading her carb log under `/sugary` — the foods she keeps, the meals she logged, the foods in each, the insulin doses she recorded and her periods ("when did she last have paella", "how did her glucose do after pizza", "how much insulin did she take for it", "was she on her period that week"). Where each table is, where its columns are explained, and how to find a food, a meal's dose or a month.
---

### Her carb log at `/sugary`

Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

- `/sugary/README.md` — what every column means. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.
- `/sugary/foods.csv` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
- `/sugary/meals/<YYYY-MM>.csv` — one row per meal she logged that month: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards.
- `/sugary/items/<YYYY-MM>.csv` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her.
- `/sugary/doses/<YYYY-MM>.csv` — one row per insulin dose she took that month: its units, `rapid` or `basal` (long-acting), and the `meal_id` of the meal it was for, if any.
- `/sugary/periods.csv` — her menstrual periods: `start_date`, `end_date` (empty while it is going), each one's length and its cycle's. `/sugary/day-notes.csv` — what she wrote about a day. Both are days on the home's calendar: read `/ha` history for those days beside them.

A month with nothing logged has no file, and a row is filed under the month its own time shows. Every time carries its offset in the home's zone, the clock the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up.

Her doses are her record of what she took, never a basis for advice. Asked what she should take, answer with what she took, the meal's logged carbs and what her glucose did, and give no number of units for any meal — not her ratio applied to its carbs, not even as arithmetic someone asked for — and no correction or timing: her dose is hers and her diabetes team's to decide.

### Finding things

- One of her foods by name: `domain__filesystem__text_search` for the name, with `path` set to `/sugary/foods.csv`.
- When she ate something: `domain__filesystem__text_search` for the name, with `directoryPath` set to `/sugary/items`; each hit carries its `meal_id` and `eaten_at`.
- What she took for a meal: `domain__filesystem__text_search` for its `meal_id`, with `directoryPath` set to `/sugary/doses`.
- Which months she logged: `domain__filesystem__glob` for `*.csv` with `basePath` set to `/sugary/meals` (or `/sugary/doses` for her insulin).
- A month whole: `domain__filesystem__file_read` of its file.
