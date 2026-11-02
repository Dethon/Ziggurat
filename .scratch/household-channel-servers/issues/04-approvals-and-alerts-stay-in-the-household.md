# 04: Approvals and alerts stay in the household

**What to build:** An approval is offered and settled only on the deployment's channels and the
asker's household's own connections. An alert (a timer with no satellite) rings, and is dismissed,
only on the deployment's channels and its owner's household's own connections. Nothing of one
household's is offered on another's devices. Spec: `../spec.md`.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] With two households each connected to a channel server that shows approvals, an approval is offered on the asker's household's connection and the deployment's, never the other household's (round trip)
- [ ] An alert rings only on the owner's household's connections and the deployment's, and a dismissal settles it there
- [ ] The home's approvals and alerts are unchanged
