# 03 — Her exercise as tables

**What to build:** Her sessions become part of the tables the log is read and shared as. The
`/sugary` mount serves `exercise/<YYYY-MM>.csv`, one row per session started that month, rendered
from the database on every read and never writable. The README explains every column. The app's
settings offer exercise as a download beside meals and doses.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/exercise/spec.md`

- [ ] Over MCP, `exercise/<YYYY-MM>.csv` exists for a month with a session and not for a month without, including a month with sessions and no meals; its columns are `exercise_id`, `started_at`, `ended_at`, `minutes`, `kind`, `name`, `note`.
- [ ] `minutes` is the rounded whole minutes from start to end; `ended_at` and `minutes` are empty for a running session.
- [ ] Listing, glob, info, read and text search cover the months like the dose months; writes under `/sugary/exercise` are refused, and the filesystem conformance row still passes.
- [ ] Times carry their offset in the home's zone; a leading formula character in a note or name is escaped.
- [ ] The README's exercise section covers the kinds and the name, minutes worked out from the times, a running session's empty end, the shared clock, and that the table is her record, never a basis for suggesting carbs or insulin.
- [ ] `GET /api/export/exercise.csv` honours the period and clock parameters like the other downloads, and settings links to it.
