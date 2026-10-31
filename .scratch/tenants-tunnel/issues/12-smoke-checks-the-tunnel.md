# 12: The smoke tier checks the tunnel

**What to build:** A wiring mistake in the tunnel is caught before a deploy: the smoke script starts the home kit beside the smoke stack, and the smoke tests prove a signed-in reply that uses a tunnelled server, a browse page through the tunnel, and a forged tunnel credential refused. Spec: `../spec.md` (Testing Decisions, seam 5).

**Blocked by:** 10, 11

**Status:** ready-for-agent

- [ ] A signed-in reply uses a tool of a tunnelled server
- [ ] A browse page answers through the tunnel
- [ ] A forged tunnel credential is refused
- [ ] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`
