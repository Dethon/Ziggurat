# 02 — Editing and deleting a dose

**What to build:** Tapping a dose in the Log opens a sheet where she can change its units, kind, time
and note, or delete it. A deletion can be undone for a moment from the toast, as recording an outcome
can. A change that is refused says why and leaves the dose as it was.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [x] Replacing a dose through the API stores the new values. The same refusals as on create apply, and a refused replace leaves the stored dose unchanged.
- [x] Deleting a dose removes it. An unknown identifier gets a 404 on replace and on delete.
- [x] The dose sheet edits every field and deletes. The toast's undo brings a deleted dose back with its values.
- [x] The timeline reflects an edit (including a time moved to another day) and a deletion without a reload.
