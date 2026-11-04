# 05: `lemonade` is never a tool server

**What to build:** A household's `lemonade` server carries chat to its Lemonade box. It is never an MCP server. The Servers page lists it with its connection state like any own server, and lets the owner mint and revoke its credential. But it is never offered to an agent template as a server, and the agents section's checks refuse an agent configuration that names it as one. The relay never dials it as MCP.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] The Servers page lists a minted `lemonade` with its connection state, and its credential can be minted and revoked like any kit server's.
- [ ] An agent template is never offered `lemonade` as an own server.
- [ ] The agents section's checks refuse an agent that names `lemonade` as a server, with a reason saying it is the household's Lemonade box, not a tool server.
- [ ] No turn's session dials `lemonade` as an MCP server, even when the household's tunnel for it is open.
- [ ] Revoking the `lemonade` credential closes its tunnel, and the household's box offers no models from the next refresh on (the existing revocation path, checked here end to end with ticket 03's hosts).
- [ ] The admin rules' Servers page section and the household configuration rules' agents checks say so.
