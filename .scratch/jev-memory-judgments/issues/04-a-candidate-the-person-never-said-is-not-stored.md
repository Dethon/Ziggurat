# 04 — A candidate the person never said is not stored

**What to build:** The check. Between extraction and the store fan-out, each candidate is judged in parallel on four nouls (`supported`, `about_user`, `durable`, `not_a_question`) over the window fields plus `candidate`. It is stored only when each clears its own configurable bar; a `MemoryCategory.Instruction` candidate is judged on `supported` alone, because an instruction is a request and the probe showed `not_a_question` rejecting every one. No answer stores as today. A drop publishes a judgment event carrying the candidate's content and its four scores. The embedding dedup runs after, unchanged, and `StoredCount` keeps meaning what was written.

**Blocked by:** 03

**Status:** resolved

- [x] A candidate scoring 0.4 on any one noul is not stored and its drop event carries content and scores; one at 0.5 on all four is.
- [x] An Instruction candidate with `supported` 0.8 and `not_a_question` 0.1 is stored.
- [x] An Instruction candidate with `supported` 0.2 is dropped — the category is not a bypass.
- [x] An absent answer for one candidate stores that candidate and does not affect the others.
- [x] Five candidates are judged concurrently, not in series.
- [x] Spec: `.scratch/jev-memory-judgments/spec.md` § B — the check.
