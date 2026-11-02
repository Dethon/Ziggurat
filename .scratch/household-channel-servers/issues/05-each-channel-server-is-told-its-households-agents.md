# 05: Each channel server is told its own household's agents

**What to build:** Each channel connection is registered with its own household's agent catalogue, not
the home's. A household's channel server learns the agents that household has, under the names and
models it runs them on, and is told again when the household's agents change. The glossary's
**household agent catalogue** entry is rewritten to say a channel connection is told its household's.
Spec: `../spec.md`.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A non-home household's channel server is registered with that household's agents, not the home's (round trip)
- [ ] Renaming or adding an agent in that household reaches its channel server
- [ ] The home's channel servers are still registered with the home's agents
