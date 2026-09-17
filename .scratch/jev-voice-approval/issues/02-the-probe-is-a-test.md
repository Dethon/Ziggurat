# 02 — The probe is a test

**What to build:** The 32 labelled answers in `probe/` become data beside a `[Trait("Category","Jev")]` test that drives the real reader against live Jev, skipped without a key: every labelled approve approves, every decline declines, every narrowed or noisy answer is Ambiguous, and no answer is decided the wrong way.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Zero wrong actions over the set is a hard assertion; the count of re-asks is reported and floored at the number measured the day it lands.
- [ ] Spec: `.scratch/jev-voice-approval/spec.md` § Solution.
