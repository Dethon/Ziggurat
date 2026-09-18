# 02 — An extraction says how it ended

**What to build:** The measurement the gate will be judged by, before the gate exists. `MemoryExtractionEvent` gains an `Outcome` — `empty`, `extracted`, `failed` now, `gated` reserved for ticket 03 — so a retry exhaustion stops publishing the same zero as a turn with nothing in it. `MemoryMetric` gains candidates and outcome so the Memory page can chart the share of turns that extract nothing, which is the baseline the gate predicts. Follow `.claude/rules/observability.md`.

**Blocked by:** None — can start immediately.

**Status:** in-progress

- [x] An extraction that exhausts its retries publishes `failed`; one that returns no candidates publishes `empty`; the existing zero-candidate test is updated, not deleted.
- [x] Events stored before this change still read, with no outcome.
- [ ] The Memory page charts outcome share and candidates per extraction, verified in a browser against the local stack.
- [ ] Spec: `.scratch/jev-memory-judgments/spec.md` § Telemetry.
