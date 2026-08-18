# 06 — Ordering constraints

**What to build:** a scenario can require that one call precedes another, without requiring a
total order. Tool invocation is concurrent by design, so asserting the whole recorded sequence
would make ordinary concurrency a failure.

Exercised by extending a running timer: the contract says read its status for the remaining time,
then delete it, then create the replacement. Unrelated calls in between are fine; the order of
those three is not.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A scenario declares "A before B" pairs and fails when B precedes A.
- [ ] Unrelated calls interleaved between a constrained pair do not fail the scenario.
- [ ] A pair whose second call never happens fails, distinguishably from an out-of-order pair.
- [ ] The extend-a-running-timer scenario passes with its three-step order declared.
- [ ] Ordering checks are proven to fail correctly against a scripted chat client.
