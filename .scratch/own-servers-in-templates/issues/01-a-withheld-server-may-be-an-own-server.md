# 01: A withheld server may be an own server

**What to build:** Prefactor, no behaviour change. A household's agents already withhold the shared
servers its features turned off, from every agent definition and every worker it spawns, by comparing
endpoint URLs. An own server is named by its bare server name (`vault`), which that comparison never
matches. Make the withheld list able to hold own server names beside shared servers' addresses, and
match an endpoint against it as a server — a URL by its server address, a name by the name — so the
next ticket can withhold own servers through the same mechanism. Spec: `../spec.md` ("Implementation
Decisions": the agent withholds what was not set up).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] An own server name in the withheld list removes that name from a household's agent definitions and from a spawned worker's endpoints; a shared server's URL still does as before
- [ ] A name never matches a URL endpoint and a URL never matches a name endpoint (a server named like a host is not withheld by accident)
- [ ] Nothing withholds an own server yet: every existing test passes unchanged
