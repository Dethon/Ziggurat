# 04 — Doses as tables

**What to build:** Her doses become part of the tables the log is read and shared as. The `/sugary`
mount serves `doses/<YYYY-MM>.csv`, one row per dose taken that month, with `dose_id`, `taken_at`,
`kind`, `units`, `meal_id` and `note`, rendered from the database on every read and never writable.
The README explains every dose column. The app's settings offer the doses as a download beside
meals, items and foods.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [ ] Over MCP, `doses/<YYYY-MM>.csv` exists for a month with doses and not for a month without, including a month with doses and no meals.
- [ ] Listing, glob, info, read and text search cover the dose months like the meal months. Text search finds a meal's doses by `meal_id`.
- [ ] Times carry their offset in the home's zone. The CSV follows the other tables' rules: RFC 4180, no byte-order mark, a leading formula character in a note escaped.
- [ ] Writes under `/sugary/doses` are refused, and the filesystem conformance row still passes.
- [ ] The README's dose section covers:
  - units are insulin units;
  - `rapid` is rapid-acting and `basal` is long-acting;
  - `meal_id` is empty for a dose not linked to a meal;
  - a dose saved with a meal carries the meal's time unless she changed it;
  - the table is her record of what she took, never a recommendation.
- [ ] `GET /api/export/doses.csv` honours the period and clock parameters like the other downloads, and settings links to it.
- [ ] The meals and items tables are byte-identical to before, and the import is unchanged.
