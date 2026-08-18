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

**Status:** ready-for-agent

- [ ] The subagent factory is stubbed and records the profile id and prompt it was asked for.
- [ ] A request with two independent parts delegates both rather than doing them in sequence.
- [ ] A single trivial lookup is done directly and delegates nothing.
- [ ] The delegated prompt contains the context the task needs and does not rely on history.
- [ ] The final reply synthesises the worker's result and does not attribute it to a worker.
- [ ] Every scenario cites its claims and was demonstrated red once.
