# 14 — Memory correction and stale-memory removal

**What to build:** remembered facts are applied silently, corrected when the user corrects them,
and never narrated.

The scenario declares the remembered facts and they ride the turn the way recall already does, so
no embedding service participates and the no-cross-provider-fallback rule stays untouched. What is
asserted is what the reply used, which memory calls followed, and what the reply did not say.

**Blocked by:** 05, 09.

**Status:** done

- [x] A scenario declares a recall block and it reaches the model as the memory contract promises.
      `Scenario.Remembered` is seeded into `EvalMemory` and set on the turn with
      `SetMemoryContext`, so the block is rendered by the same `TurnDecoration` production uses.
      The memory feature is registered the way both shipped assistants enable it, which also puts
      the memory section back in the assembled prompt.
- [x] A declared fact is used in the answer without the reply mentioning memory, remembering or
      forgetting. "Pon un temporizador para la pasta" against a remembered nine minutes comes back
      as 540 seconds, with the reply silent about where the number came from.
- [x] A user correction results in the stale fact being removed — and the fact beside it survives,
      which is the half a call log cannot show: a forget takes a query and deletes everything the
      search reached.
- [ ] …and the new one recorded. Withdrawn: recording is the extraction pipeline's job — a
      separate model call in a background worker reading the *persisted* message, which no prompt
      claims and which the eval's boundary (one turn, one agent) does not contain.
- [x] A turn asking to forget something removes it and confirms without narrating the mechanism.
- [ ] Every scenario cites its claims and was demonstrated red once. All three were demonstrated
      and **none of them stayed red**, so all three citations were withdrawn and the findings are
      in `ClaimExemptions`: the silent-application sentence, the correction bullet (deleted from
      the prompt *and* from the forget tool's description), and finally the whole "When to forget"
      section were each removed, and the model behaved identically every time. The scenarios run
      as regression guards against a model without those defaults.

**What this family measured, beyond the checkboxes:** memory's prose is the least load-bearing
of any family evaluated so far. Nine claims are declared; four were never citable (see the
exemptions), and the five that were cited all failed their demonstration. What actually carries
the behaviour is the recall block itself and the forget tool's description — neither of which can
be deleted without deleting the capability.
