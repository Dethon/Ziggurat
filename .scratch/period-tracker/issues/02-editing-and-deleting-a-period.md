# 02 — Editing and deleting a period

**What to build:** Tapping a past or ongoing period opens a sheet to change its start, its end, or
make it ongoing again, and to delete it. A refused change says why and leaves the period as it was.
Deleting it is undone from the toast with the dates she recorded.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/period-tracker/spec.md`

- [ ] Delete route; an unknown period is not found on replace and delete.
- [ ] A refused replace leaves the period as it was.
- [ ] The sheet's undo restores the period with its dates.
