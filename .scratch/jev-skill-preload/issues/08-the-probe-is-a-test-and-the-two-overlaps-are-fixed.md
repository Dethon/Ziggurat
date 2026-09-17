# 08 — The probe is a test, and the two overlaps it found are fixed

**What to build:** `probe/jev_probe.py`'s skill half becomes a test over the real preloader and policy: the labelled set as data, run against live Jev, asserting **no wrong preload** and a coverage floor. `[Trait("Category", "Jev")]`, run only when a TypeSafe key is present, like `Category=Llm` — a pass costs well under a cent. Then the two misses the probe found are fixed where the spec says they are fixed, in the descriptions: "para la alarma" (Jev said home-assistant 0.83; the timers skill owns whatever is ringing) and "dime cuando termine la lavadora" (home-assistant 0.74; a watch). Both fell under the bar, so neither is a wrong preload today — this is coverage. A description edit is a trigger-claim edit: regenerate the snapshots, read them, and run the touched families armed on the changed tier.

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] The labelled set lives beside the test as data, Spanish and English, with the two-skill and no-skill cases kept.
- [ ] The test asserts zero wrong preloads over the set and fails below the coverage floor measured on the day it lands.
- [ ] Without a key the test is skipped with a reason, never red.
- [ ] After the description edits both phrases preload the right skill, no other case regressed, and the description budgets still hold.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § Solution (measurements), § Out of Scope (no second criteria text).
