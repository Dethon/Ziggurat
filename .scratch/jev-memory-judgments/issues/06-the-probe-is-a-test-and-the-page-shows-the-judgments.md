# 06 — The probe is a test, and the page shows the judgments

**What to build:** `probe/jev_memory_probe.py`'s three labelled sets become data beside a `[Trait("Category","Jev")]` test that drives the real question builders against live Jev, skipped with a reason when there is no key. Synthetic data only — no production conversation is ever added to it. The Memory page gains drops and refused merges beside ticket 02's charts. The rule file gains the three judgments, where each sits, and that each fails toward today's behaviour.

**Blocked by:** 03, 04, 05

**Status:** resolved (the week-after re-read is pending deploy)

- [x] Gate: zero false skips over the windows labelled as holding a memory; the skip share over the empty ones is reported, with a floor set from the day it lands.
- [x] Check: every labelled keeper kept, every labelled junk dropped, the two instruction cases included.
- [x] Pairs: every link decision right except the recorded "Laura" case, which is allowed only to fail toward not linked. (A second borderline pair, "Reads light novels", answered `same` at 0.32 on the probe day and `distinct` at ~0.6 on 2026-09-18 under either wording; it is recorded the same way, allowed only to fail toward not linked.)
- [x] The existing `Category=Llm` memory tests pass with `judgments.enabled: true`.
- [x] The page shows drops (with the dropped text) and refused merges, verified in a browser.
- [ ] A dated note under the spec's `## Comments` a week after deploy re-reads the bars from the events.
- [ ] Spec: `.scratch/jev-memory-judgments/spec.md` § Acceptance.
