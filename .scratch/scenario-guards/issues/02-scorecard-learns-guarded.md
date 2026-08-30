# 02 — The scorecard learns `guarded`

**What to build:** A scorecard reader can tell a guarded claim from an untested one: the per-claim coverage field resolves by precedence `cited > judged > conditional > guarded > exemption > uncovered`, so a claim covered only by a guard reads `guarded`, and a claim both cited and guarded reads `cited` — the pair is a truth with a ranking, never a contradiction, and no test forbids it.

Spec: `.scratch/scenario-guards/spec.md`.

**Blocked by:** 01 — The Guards declaration and its invariants.

**Status:** resolved

- [x] A claim guarded by a scenario and cited by none reads `guarded` in the written scorecard, asserted through the scorecard JSON in the existing ledger-test style (the `conditional` test is the pattern)
- [x] A claim both cited by one scenario and guarded by another reads `cited`
- [x] A claim guarded and also conditionally cited reads `conditional` — guard ranks below every citation flavour
- [x] Exemption kinds and the `uncovered` fallback are unchanged for claims no scenario touches
- [x] Deterministic, bare `dotnet test`; TDD triplet with the red observed first

## Comments

- 2026-08-30 — Done in 05cd33db: coverage precedence cited > judged > conditional > guarded > exemption > uncovered; ledger takes an optional synthetic suite so the cited+guarded pair is assertable through the written scorecard.
