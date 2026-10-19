# 01 — Sugary's tables as one typed schema

**What to build:** Sugary's seven tables (meals, items, foods, doses, exercise, periods, day notes)
are defined once as a typed schema — a generic table model of named, typed columns and rows — and
the CSV everyone reads today is one encoding of it. Nothing a caller sees changes: the app's export,
its import, its dose and exercise rules and the `/sugary` mount all get the same CSV, byte for byte.
This is the prefactor the query and the Parquet files are built on, so the three encodings cannot
drift.

**Blocked by:** None (can start immediately)

**Status:** done

Spec: `.scratch/sugary-query/spec.md`

- [x] A generic table model exists in Domain: a table has a name, ordered columns each with a type (text, integer, decimal, boolean, instant, home-clock timestamp, date) and rows; nothing in it is Sugary's.
- [x] Each Sugary table is built as that model from the log, with the same row order, column order and column names as today's CSV.
- [x] A CSV encoding of the model reproduces today's output byte for byte for every table, including the offset-carrying stamps, empty cells, enum spellings and formula escaping — pinned by tests written against today's output before the refactor.
- [x] `SugaryExportApiTests`, the import tests and `SugaryFileSystemTests` pass unchanged.
- [x] Nothing else in the repo calls the old string-returning renderers.

## Comments

- The column types are text, integer, decimal, boolean, instant and date: no home-clock timestamp
  type. An instant is held on the clock the table is read on (its offset is the home's), so the
  `_local` siblings ticket 02 adds are that instant's wall time, worked out by the engine from any
  instant column rather than declared per table — the CSV, which carries no `_local` columns, stays
  as it was without a rule to leave them out.
