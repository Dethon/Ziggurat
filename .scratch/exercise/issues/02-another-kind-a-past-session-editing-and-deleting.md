# 02 — Another kind, a past session, editing and deleting

**What to build:** Everything beyond the two shortcuts. Otro starts a session she names, offering the
names she used before. A session she forgot to start goes in afterwards with its start and minutes. A
session opened from the timeline or the running card can be changed or deleted, with undo from the
toast; a session left running past twelve hours is stopped there by saying when it ended.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/exercise/spec.md`

- [ ] `GET /api/exercise/names` answers the names of her `other` sessions, each once, most recently used first, at most eight, behind the access gate.
- [ ] `DELETE /api/exercise/{id}` deletes a session, and an unknown one is not found.
- [ ] Otro opens a sheet with a name field and her earlier names as chips; Start starts the session as the shortcuts do.
- [ ] Add a past session opens the sheet with the kind, a start an hour ago, minutes empty (never prefilled) and a note; the minutes read as she typed them give the end (vitest), and a refusal says why.
- [ ] A session opened from its row or the running card can change its kind, name, start, minutes and note; a running one can be given its end there.
- [ ] Deleting a session is undone from the toast, saved again with every value she recorded (vitest on the restored session).
- [ ] Every new string is in English and Spanish.
