# 12: A glucose watch's prompt reaches the agent

**What to build:** `DelegatedWrites` signs for writes to `sugary-watches`; Sugary keeps the delegation with the watch and serves `delegations_list`/`delegations_replace` (`AddDelegationStore`); the verifier gains the delegated kind. A fired prompt effect is composed by `FirePlanning` (principal = `userId`, household, Watch origin with the id, report beside the prompt, `deliverTo`) and emitted on Sugary's channel. A fire no agent took keeps a `once` watch armed and is logged. Spec: "Prompt effect".

**Blocked by:** 11

Status: ready-for-agent

- [ ] Over a real inbox: a fire arrives with the delegation, verifies, and runs as the watch's person.
- [ ] A watch written by a turn for nobody fires for nobody; redelegation replaces the stored token.
- [ ] `once` is spent only by a fire an agent took.
