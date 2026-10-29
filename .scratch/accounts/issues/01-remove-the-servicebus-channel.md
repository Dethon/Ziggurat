# 01: Remove the ServiceBus channel

**What to build:** No channel lets a message name its own sender any more: the ServiceBus channel server goes, with its deployment entry, its placeholder, the agent's channel endpoint for it, its tests and its mentions in rules and README. Prefactor: one fewer channel to move onto accounts. Spec: `../spec.md` ("Channels and the principal", ServiceBus).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] The solution builds and the suite passes with the channel gone
- [ ] No deployment, settings, rule or README reference to it remains
