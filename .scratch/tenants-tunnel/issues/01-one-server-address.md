# 01: One server address every host-keyed lookup reads

**What to build:** Everything that today keys on a dialled URL's host — the call-token audience, the per-server credential lookup, the tool qualifier, the prompt cache, the deployment endpoint list, the outpost verdicts — reads one server address instead, so a later ticket can give a tunnelled server a synthetic address without touching any of them. Prefactor, no behaviour change. Spec: `../spec.md` ("The tunnel").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Every host-keyed lookup goes through the one server address, and a check fails if code derives a server's identity from a URL's host itself
- [ ] Every existing server, channel, eval and deployment test passes unchanged
