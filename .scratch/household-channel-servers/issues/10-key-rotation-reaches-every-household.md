# 10: Key rotation reaches every household's channel servers

**What to build:** Redelegating and retiring signing keys ask every household's open channel servers
that hold delegations (Home Assistant's watches, the library's downloads, web search's logins), not
only the home's. The command lists each by household and server name. A server that is not connected
is reported as not asked, so the operator knows whose delegations need a second pass. Spec:
`../spec.md`.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Redelegation reaches a non-home household's Home Assistant server over its tunnel (round trip, redelegation over the tunnel)
- [ ] A household's server that is not connected is listed as not asked, under its household
- [ ] The home's servers are asked and reported as today
