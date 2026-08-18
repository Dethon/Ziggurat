# 16 — Delegation

**What to build:** the parent's decision to delegate is what gets tested. The subagent factory is
stubbed with a canned result, so a bad worker answer cannot fail a scenario that is about the
parent.

Delegation happens for parallel or heavy work, does not happen for a single lookup faster done in
place, names an existing profile, and carries a self-contained prompt — the worker has no
conversation history, so every url, name and requirement the task needs must be in the prompt the
parent wrote. The answer is synthesised rather than pasted back, and never says which worker did
what.

**Blocked by:** 04, 05.

**Status:** partially-done — the positive half needs a turn this model reliably delegates

- [x] The subagent factory is stubbed and records the profile id and prompt it was asked for.
- [ ] A request with two independent parts delegates both rather than doing them in sequence.
      **Written, run and withdrawn — it does not hold.** Asked to summarise two unrelated vault
      folders "a la vez, por separado", the model delegated both halves on one run and on the next
      two did the work itself: 17 sequential reads in its own turn. Making each folder eight notes
      instead of two did not change it. The scenario is not in the suite because it would be a red
      test rather than a guard; the finding is in `ClaimExemptions` and is the most useful thing
      this family produced.
- [x] A single trivial lookup is done directly and delegates nothing.
- [x] The delegated prompt contains the context the task needs and does not rely on history — the
      check exists and is proven deterministically (a declared delegation whose prompt lacks what
      the task needs fails, and two declared tasks are not satisfied by one worker told to do
      both). What it lacks is a scenario that reliably delegates.
- [ ] The final reply synthesises the worker's result and does not attribute it to a worker.
      Blocked by the same thing: it can only be asserted on a turn that delegated.
- [x] Every scenario was demonstrated red once, and the one that ships cites nothing: with the
      do-it-yourself bullet deleted the model still read the timer's status itself.

Deviation from the spec, on purpose: this adds a **second** production seam where the spec said
there would be one. `ISubAgentSpawner` is the same shape as the tool observer — an optional
interface nothing in a deployment registers — and it exists because the alternative is paying a
second model to answer every delegation scenario and letting its answer decide whether the parent
passed.
