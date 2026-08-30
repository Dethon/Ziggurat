# 01 — The Guards declaration and its invariants

**What to build:** A scenario can declare the claims it guards — asserts without evidencing — beside the claims it cites, each with a mandatory note, and the deterministic coverage tests treat a guarded claim as covered while policing the declaration the way they police citations and exemptions. Expand only: no existing exemption entry moves, and everything stays green with zero guards declared.

Spec: `.scratch/scenario-guards/spec.md`. ADR-0031's amendment and the glossary's **Guard**/**Exemption** entries are already in the tree — follow their vocabulary.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] `Scenario` gains a `Guards` list of a record pairing a guarded claim id with a mandatory freeform note; the demonstration date stays a convention inside the note, not a field
- [ ] More than one scenario may guard the same claim without any check objecting
- [ ] Red-first: a guard naming a claim no section declares fails the coverage tests with a message naming the scenario and the invented claim
- [ ] Red-first: a guard with a blank note fails the same way a blank exemption reason does
- [ ] A claim covered only by a guard no longer trips the every-claim-has-a-scenario-or-exemption test
- [ ] The covered-and-excused test now fails an exemption entry for a claim any scenario cites *or guards* — the residual list may not claim a watched claim is untouched
- [ ] All new checks run on a bare `dotnet test` with no model, container, or armed run
- [ ] TDD triplet: failing test observed before each behaviour lands; suite green at the end

## Comments
