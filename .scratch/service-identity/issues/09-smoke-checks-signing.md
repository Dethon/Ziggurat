# 09: The smoke tier checks signing end to end

**What to build:** A key or wiring mistake is caught before a deploy. The smoke tests prove on a real stack that a call to a server with a forged `_meta` principal and no token is refused, and that a signed-in reply, Sugary and the dashboard work with assertions verified. Spec: `../spec.md` (Testing Decisions, seam 6).

**Blocked by:** 05, 06, 07

**Status:** ready-for-agent

- [ ] A forged call from inside the network is refused by a server
- [ ] A signed-in reply, Sugary's API and the operator's dashboard pass through verified assertions
- [ ] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`
