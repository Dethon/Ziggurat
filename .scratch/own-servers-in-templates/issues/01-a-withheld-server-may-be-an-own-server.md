# 01: A withheld server may be an own server

**What to build:** Prefactor, no behaviour change. A household's agents already withhold the shared
servers its features turned off, from every agent definition and every worker it spawns, by comparing
endpoint URLs. An own server is named by its bare server name (`vault`), which that comparison never
matches. Make the withheld list able to hold own server names beside shared servers' addresses, and
match an endpoint against it as a server — a URL by its server address, a name by the name — so the
next ticket can withhold own servers through the same mechanism. Spec: `../spec.md` ("Implementation
Decisions": the agent withholds what was not set up).

**Blocked by:** None (can start immediately)

**Status:** done

- [x] An own server name in the withheld list keeps a turn from reaching that server or naming it offline — an agent's and a spawned worker's, which carries its parent's list; a shared server's URL is still removed from the definitions as before
- [x] The name is applied where a turn resolves its own servers, not to the definitions: an agent outlives its conversation's turns, so a name taken out of a definition could never come back once the household set the server up (spec story 10)
- [x] A name never matches a URL endpoint and a URL never matches a name endpoint (a server named like a host is not withheld by accident)
- [x] Nothing withholds an own server yet: every existing test passes unchanged
