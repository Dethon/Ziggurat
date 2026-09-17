# 06 — The probe is a test, and the page shows the judgments

**What to build:** `probe/jev_memory_probe.py`'s three labelled sets become data beside a `[Trait("Category","Jev")]` test that drives the real question builders against live Jev, skipped with a reason when there is no key. Synthetic data only — no production conversation is ever added to it. The Memory page gains drops and refused merges beside ticket 02's charts. The rule file gains the three judgments, where each sits, and that each fails toward today's behaviour.

**Blocked by:** 03, 04, 05

**Status:** ready-for-agent

- [ ] Gate: zero false skips over the windows labelled as holding a memory; the skip share over the empty ones is reported, with a floor set from the day it lands.
- [ ] Check: every labelled keeper kept, every labelled junk dropped, the two instruction cases included.
- [ ] Pairs: every link decision right except the recorded "Laura" case, which is allowed only to fail toward not linked.
- [ ] The existing `Category=Llm` memory tests pass with `judgments.enabled: true`.
- [ ] The page shows drops (with the dropped text) and refused merges, verified in a browser.
- [ ] A dated note under the spec's `## Comments` a week after deploy re-reads the bars from the events.
- [ ] Spec: `.scratch/jev-memory-judgments/spec.md` § Acceptance.
