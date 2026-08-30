# 03 — An unseeable approval is denied, not awaited

**What to build:** A request-mode approval that finds no live session is answered immediately with a denial naming the reason, instead of being registered under a value that matches nothing, rendered to nobody, and awaited forever. A scheduled agent reaching an approval-gated tool with no browser connected now completes its run, and the denial is readable in the persisted reply. Notify-mode stays fire-and-forget and simply does not render. This closes the deadlock ADR-0035's contract names: a question nobody can see must not hold the turn.

**Blocked by:** 02 — The SignalR server carries the identity.

**Status:** ready-for-agent

- [ ] Red-first: a test drives a request-mode approval with no live session and fails today by never completing (bounded by the test, not the code); it passes by receiving the denial.
- [ ] The denial's text names the reason (no live session to approve in) so the persisted reply explains itself.
- [ ] No pending-approval entry is left behind on the miss path — nothing to leak, nothing for the cancellation sweep to miss.
- [ ] Notify-mode on the same miss path still returns without waiting and renders nothing.
- [ ] Approvals with a live session behave exactly as before — existing approval tests green untouched.
