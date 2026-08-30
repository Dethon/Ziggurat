# 03 — The data moves

**What to build:** Every single-owner exemption entry becomes a guard declared by the scenario its prose already names, so a maintainer editing a scenario finds its guard note in the same file and the exemption list stops restating what scenarios know. Twenty-six of the twenty-nine entries move; the note prose moves **verbatim** — improving wording is out of scope. The cross-reference comments on the scenario side ("see ClaimExemptions", sixteen of them) are deleted in the same move: the declaration now says it. The residual list is left holding exactly the three diffusely-guarded claims (probe calls and the two reply-shape claims), still under their current kind — reclassification is ticket 04's job.

Spec: `.scratch/scenario-guards/spec.md`. Where the architecture-review report and the spec disagree, the spec wins.

**Blocked by:** 01 — The Guards declaration and its invariants; 02 — The scorecard learns `guarded`.

**Status:** ready-for-agent

- [ ] Each of the 26 single-owner entries is a guard on the scenario its prose names, note text verbatim; entries asserted on two scenarios (the raw-content claim's two halves) are declared on both
- [ ] The exemption list holds exactly three entries afterwards: probe calls, the reply length limit, the no-narration rule
- [ ] No "see ClaimExemptions" cross-reference comment remains in any scenario file
- [ ] Bare `dotnet test` green — a mis-migrated entry surfaces through 01's invariants (uncovered claim, or residual-list intersection), so this ticket adds no tests of its own
- [ ] The written scorecard shows `guarded` for the 26 migrated claims and the residual three keep their exemption spelling

## Comments
