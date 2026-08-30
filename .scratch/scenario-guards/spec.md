# Spec — A guard is declared by the scenario that asserts it

Status: ready-for-agent

Origin: `.scratch/architecture-review/report.md`, Candidate 3 ("Move the guard onto the scenario"), grilled to a settled design on 2026-08-30. The grilling corrected the report twice, and both corrections bind: the residual exemption list is **not** empty after the move (three claims are guarded diffusely and stay), and the covered-and-excused test is **not** deleted (it survives shrunk, policing the residual list). ADR-0031 carries a dated in-place amendment recording the decision, and the glossary already pins **Guard** and **Exemption** — both are in the working tree beside this spec.

## Problem Statement

Twenty-nine of twenty-nine entries in the claim exemption list carry the same kind, and twenty-six of them describe something that is not an exemption at all: a scenario runs and asserts the claim's behaviour — it just cannot earn the citation, because the demonstrated-red bar was tried and deleting the prose stayed green. Each of those facts is stated twice: once as prose in a static table keyed by claim id, once as a comment on the scenario that guards it, ending in a hyperlink a human has to walk. Neither statement compiles against the other. The table has been rewritten nearly five times over its life, and the recent churn is scenario-driven — a scenario changes its shape and prose a file away has to be edited to match. A dedicated test exists solely to police that the table and the scenarios do not contradict each other, five of the six declared exemption kinds have zero entries, and the invariant "removing a line here is what adding a scenario looks like" lives in a comment because the shape cannot hold it.

## Solution

The guard relationship moves onto the scenario, where the compiler already lives. A scenario declares the claims it guards beside the claims it cites, each with the demonstration note that today sits in the table; the claim id is a compile-time symbol, so a renamed or withdrawn claim breaks the scenario that guards it, in the file that guards it. The exemption list shrinks to its designed job — claims no scenario touches at all — which after the move holds exactly the three diffusely-guarded claims no single scenario can own. The scorecard's coverage field learns the word `guarded`, ranked below the citation flavours and above exemptions, and the exemption taxonomy keeps only the kinds that still have users.

## User Stories

1. As a maintainer, I want a scenario's guard declared in the scenario's own file, so that editing the scenario and editing its guard note is one commit touching one file.
2. As a maintainer, I want a guard's claim id to be a compile-time symbol, so that renaming or withdrawing a claim breaks the guarding scenario at build time instead of rotting a table entry.
3. As a maintainer, I want the sixteen "see ClaimExemptions" comments replaced by the declaration itself, so that no fact about a scenario is stated in prose a file away.
4. As a maintainer, I want the exemption list to hold only claims no scenario touches, so that reading it is reading the backlog and nothing else.
5. As a maintainer, I want exemption kinds with zero entries deleted, so that the taxonomy states only distinctions the data actually draws.
6. As a prompt author, I want a guarded claim distinguishable from an uncovered one, so that I know the behaviour is asserted every run even though the prose was never proven to earn it.
7. As a prompt author, I want the guard note to record how the demonstration went, so that the reason a claim cannot be cited is readable beside the scenario that tried.
8. As a prompt author, I want a claim guarded by two scenarios to be declarable on both, so that neither declaration has to lie about being the only witness.
9. As a scorecard reader, I want a guarded claim's coverage to read `guarded`, so that a null-free rate on an uncited claim is legible as watched rather than untested.
10. As a scorecard reader, I want a claim both cited and guarded to read `cited`, so that the strongest true statement wins and the pair is never reported as a contradiction.
11. As a scorecard reader, I want the three diffusely-guarded claims to keep an honest exemption kind, so that the backlog distinguishes a scenario worth writing from one deliberately not required.
12. As a test author, I want a guard naming an undeclared claim to fail the deterministic coverage tests, so that a guard cannot outlive the claim it watches.
13. As a test author, I want a guard with a blank note to fail the same way an exemption with a blank reason does, so that the demonstration record cannot silently go missing.
14. As a test author, I want an exempted claim that any scenario cites *or guards* to fail the coverage tests, so that the residual list can never claim a watched claim is untouched.
15. As a test author, I want every guard invariant checked on a bare `dotnet test` with no model and no container, so that the checks cost what the existing coverage tests cost.
16. As a reviewer of a model bump, I want guard declarations riding the scenario rates the ledger already records, so that a guard's drift stays a diff with a number.
17. As a reviewer, I want the migration to land as small green steps — the type and its tests first, the data move second, the shrink last — so that any regression bisects to one commit.
18. As a future reader, I want ADR-0031's amendment and the glossary to carry the guard/exemption distinction, so that "either a scenario or a line in the exemption list" is read as amended, not violated.

## Implementation Decisions

All settled in the grilling; none are open.

- **Placement is forced, not preferred.** Claims are declared in the domain layer and scenarios in the eval project, and the dependency points one way — so a compile-checked guard has exactly one possible home: the scenario. A claim-side back-reference could only ever be prose, which is the shape being removed.
- **The declaration.** `Scenario` gains a `Guards` list beside `Claims`, of a small record pairing the guarded claim id with a mandatory note. The note stays freeform prose; the demonstration date remains a convention inside it, not a field. More than one scenario may guard the same claim.
- **Coverage is a precedence, not a partition.** The scorecard's per-claim coverage resolves `cited > judged > conditional > guarded > exemption > uncovered`. A claim both cited and guarded is a truth — one scenario demonstrated red, another asserts the same behaviour as a side condition — so no test forbids the pair; the citation outranks the guard. The coverage string for a guard is `guarded`; the one-time diff noise against archived scorecards is accepted.
- **The residual list is not empty.** Three claims are guarded diffusely — by every scenario's exhaustive permitted set, or by every spoken scenario's declared reply limit — and owned by no one; assigning them to one scenario would be arbitrary. They stay exemptions under the kind their prose earns: the probe-call claim as deliberately-not-required, the two reply-shape claims as unwritten. Their reasons keep noting the diffuse assertion.
- **The taxonomy keeps only kinds with users.** After the move that is the unwritten and not-falsifiable kinds. The guard kind is deleted by design; the fixture-blocked, judge-awaiting and finding kinds are deleted for having no entries — a kind returns with its first user, and git history keeps the prose. The scorecard's kind-spelling function shrinks accordingly.
- **The migration is verbatim.** Each of the twenty-six single-owner entries moves its prose unchanged onto the scenario its prose already names — the "asserted as a side condition" and "demonstrated by deletion" flavours are both just notes; the sixteen cross-reference comments are deleted in the same move rather than left to restate the declaration beside itself.
- **The invariants move with the shape.** The covered-set used by the deterministic coverage tests grows guards; the exists-and-has-a-reason checks extend to guards; the covered-and-excused test survives shrunk, now asserting the residual list intersects neither the cited set nor the guarded set. Nothing about the demonstrated-red bar for *citing*, the run policy, the judge, or the armed-run machinery changes.
- **Documentation is already done.** ADR-0031's dated in-place amendment and the two glossary entries are in the working tree; the implementing change lands beside them and adds no new ADR.
- **Migration order: three green steps, TDD each.** (1) The `Guards` declaration plus the extended coverage tests, red-first via a guard naming an invented claim and a blank note. (2) The scorecard precedence and the `guarded` string, asserted through the written scorecard. (3) The data move — twenty-six entries onto their scenarios, cross-reference comments deleted, the diffuse three reclassified, the four userless kinds deleted.

## Testing Decisions

- Everything here is deterministic and runs on a bare `dotnet test` — no model, no container, no armed run. A good test asserts the observable contract (a coverage failure message, a scorecard field) and never the shape of the table behind it.
- **Two seams, both existing, no new ones.** The suite-level coverage tests (`ClaimCoverageTests`) police the invariants: a guard names a declared claim, its note is non-empty, and the residual exemption list intersects neither cited nor guarded claims. The ledger tests (`ScorecardLedgerTests`) assert the written scorecard's coverage field: a guarded claim reads `guarded`, and a claim both cited and guarded reads `cited` — the same style as the existing `AConditionallyCitedClaim_IsCoveredAsConditional`.
- Prior art: the existing coverage tests' invented-claim and blank-reason checks are the exact pattern the guard checks extend; the ledger tests already read the coverage string off the JSON the run writes.
- The migration commit needs no tests of its own: it is data movement whose correctness the two seams already decide — a mis-migrated entry surfaces as an uncovered claim or a residual-list intersection.

## Out of Scope

- **The tolerance profile** (the report's deferred item). It follows this change onto the widened `Scenario`; nothing here anticipates its shape.
- **Label-ladder** (Candidate 2) — shares no files; fully independent.
- The demonstrated-red bar for citations, the k-of-N run policy, the judge, the smoke/full tiers, and every armed-run behaviour.
- The claim declarations themselves and their domain-side shape — no claim moves, renames, or changes kind.
- The scorecard's schema beyond the coverage strings; rates, seeding and the route recording are untouched.
- Rewriting any guard note's content — prose moves verbatim; improving it is a separate, per-note editorial decision.

## Further Notes

- The report's Candidate 3 section must not be taken over this spec where they disagree: its "on today's data that list is empty" and "the test is deleted rather than maintained" claims were both falsified by reading the twenty-nine entries.
- The residual test's failure message ("the backlog is lying about how much is left") is worth keeping in spirit: after the move it is the one place the exemption list's honesty is enforced.
- Scorecards in the gitignored eval output archived before this change will show `guard` where new runs say `guarded`; the reclassified three flip from `guard` to `unwritten`/`unfalsifiable`. This is a known, accepted one-time discontinuity for anyone diffing across the change.
