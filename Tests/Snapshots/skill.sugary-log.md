# sugary-log — description 97 / 100 tokens, body 571 / 600 tokens, served by mcp-sugary

================================================================================================

---
name: sugary-log
description: Reading her carb log under `/sugary` — the foods she keeps, the meals she logged, the foods in each and the insulin doses she recorded ("what is in her usual breakfast", "when did she last have paella", "how did her glucose do after pizza", "how much insulin did she take for it"). Where each table is, where its columns are explained, and how to find a food, a meal's dose or a month.
---

### Her carb log at `/sugary`

Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

- `/sugary/README.md` — what every column means. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.
- `/sugary/foods.csv` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
- `/sugary/meals/<YYYY-MM>.csv` — one row per meal she logged that month: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards.
- `/sugary/items/<YYYY-MM>.csv` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her.
- `/sugary/doses/<YYYY-MM>.csv` — one row per insulin dose she recorded taking that month: its units, `rapid` (a meal or a correction) or `basal` (long-acting, her background dose), and the `meal_id` of the meal it was for, if any.

A month with nothing logged has no file, and a row is filed under the month its own time shows. Every time carries its offset in the home's zone, the clock the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up.

Her doses are her record of what she took: read them and report them, and treat them as never a basis for suggesting a dose, a ratio, a correction or a timing, or for saying what she should have taken.

### Finding things

- One of her foods by name: `domain__filesystem__text_search` for the name, with `path` set to `/sugary/foods.csv`.
- When she ate something: `domain__filesystem__text_search` for the name, with `directoryPath` set to `/sugary/items`; each hit carries its `meal_id` and `eaten_at`.
- What she took for a meal: `domain__filesystem__text_search` for its `meal_id`, with `directoryPath` set to `/sugary/doses`.
- Which months she logged: `domain__filesystem__glob` for `*.csv` with `basePath` set to `/sugary/meals` (or `/sugary/doses` for her insulin).
- A month whole: `domain__filesystem__file_read` of its file.
