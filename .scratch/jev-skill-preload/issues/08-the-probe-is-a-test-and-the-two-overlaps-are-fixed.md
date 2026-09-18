# 08 — The probe is a test, and the two overlaps it found are fixed

**What to build:** `probe/jev_probe.py`'s skill half becomes a test over the real preloader and policy: the labelled set as data, run against live Jev, asserting **no wrong preload** and a coverage floor. `[Trait("Category", "Jev")]`, run only when a TypeSafe key is present, like `Category=Llm` — a pass costs well under a cent. Then the two misses the probe found are fixed where the spec says they are fixed, in the descriptions: "para la alarma" (Jev said home-assistant 0.83; the timers skill owns whatever is ringing) and "dime cuando termine la lavadora" (home-assistant 0.74; a watch). Both fell under the bar, so neither is a wrong preload today — this is coverage. A description edit is a trigger-claim edit: regenerate the snapshots, read them, and run the touched families armed on the changed tier.

**Blocked by:** 03

**Status:** resolved

- [x] The labelled set lives beside the test as data, Spanish and English, with the two-skill and no-skill cases kept.
- [x] The test asserts zero wrong preloads over the set and fails below the coverage floor measured on the day it lands.
- [x] Without a key the test is skipped with a reason, never red.
- [x] After the description edits both phrases preload the right skill, no other case regressed, and the description budgets still hold.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § Solution (measurements), § Out of Scope (no second criteria text).

## Answer

Shipped 2026-09-18.

- `Tests/Integration/Skills/JevSkillPreloadTests.cs` (`[Trait("Category", "Jev")]`) runs the labelled set in
  `jev-skill-cases.json` beside it (34 cases, Spanish and English, the two two-skill and seven no-skill cases kept)
  through the real `SkillPreloader` + `TypeSafeJudge` over the seven shipped skills (`AgentPromptFixture.ServedSkills`)
  at the shipped bars; skipped with a reason without `typeSafe:apiKey` (user secrets) or `TYPESAFE_API_KEY`.
  Three assertions: zero wrong preloads; coverage (expected set ⊆ preloaded, nothing extra) ≥ 0.85; the two
  overlap phrases preload the right skill. A pass is ~34 calls, well under a cent.
- Measured before the description edits, under the shipped framing ("`request` is a request made to a home
  assistant"): 0 wrong, 28/34 covered — the two overlaps plus four the probe also had under 0.9 ("apaga el aire
  dentro de una hora", "busca cuánto tiene que reposar el gazpacho", and the second skill of both two-skill cases).
- Fixes, probed phrase by phrase before landing (`para la alarma` needed both sides): the timers description leads
  with "Stopping an alarm or timer that is ringing right now (\"stop the alarm\", \"para la alarma\")"; the home
  description says "Never for stopping a ringing alarm or timer (\"stop the alarm\" is `/timers`) … or a watch
  (\"tell me when the washer finishes\")", trimmed elsewhere ("move the trash alarm", "whatever it was created as")
  to stay at 150/150; the watches description says "being told, or having something done, when …". After: 0 wrong,
  30/34 covered, both overlaps at ≥0.93. Timers 126/130, watches 104/120, all budgets hold; snapshots regenerated.
- The armed run: the description edits touch the timers, home and watches families' trigger claims; rather than a
  changed-tier run, the four full exhaustive passes of ticket 09 cover them on both models.
