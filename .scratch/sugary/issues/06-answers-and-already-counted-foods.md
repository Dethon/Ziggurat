# 06 — Answers and already-counted foods

**What to build:** When she taps answers to the estimate's questions, the app sends them as a follow-up turn on the same conversation and gets a new estimate that keeps what was established. Foods she added herself (barcode, label, her foods, by hand) travel with every turn as already counted, with their carbs, and are never listed again by the estimate.

**Blocked by:** 04

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] Answers arrive on the same conversation and the new estimate reflects them.
- [ ] A question already answered is not asked again.
- [ ] An amount she picked keeps medium confidence and a range; an amount she typed is treated as a fact.
- [ ] Already-counted foods appear on the turn and never come back as estimated items; her own numbers are never changed.
- [ ] The first estimate is still recorded with the meal when she saves it after answering.
