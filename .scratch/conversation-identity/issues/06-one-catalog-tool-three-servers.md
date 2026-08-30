# 06 — One catalog tool, three servers

**What to build:** The three byte-identical agent-registration tools (Telegram, voice, scheduling) collapse into one shared catalog-writing tool in the hosting assembly — constructor-injecting the mutable catalog, one generic description, registered explicitly per the never-assembly-scanning rule, on the no-outbound-surface precedent. The two outliers stay: the SignalR one keeps its hub broadcast, the library one keeps its no-op. Telegram's explanatory comment about why it holds a catalog at all moves to its registration site so the reasoning survives the deleted file.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Red-first: a test for the shared tool asserts it replaces the catalog and returns the registered count.
- [ ] Telegram, voice and scheduling register the shared tool; their local copies are deleted.
- [ ] The SignalR and library tools are unchanged, their tests untouched.
- [ ] The one server table's contract tests stay green — every server still resolves its settings, registers the host, and holds exactly one call-tool filter.
- [ ] Telegram's catalog-purpose comment lives at its registration site.
- [ ] The hosting assembly's dependency rule holds: Domain and the MCP packages only, no Infrastructure.
