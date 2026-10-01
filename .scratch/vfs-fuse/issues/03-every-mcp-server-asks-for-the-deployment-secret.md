# 03 — Every MCP server asks for the deployment secret

**What to build:** Every deployment MCP server, channel servers included, refuses a call to `/mcp` that does not present one deployment-wide secret. The agent reads the secret once from one setting and presents it on every configured endpoint, through the route outposts already use. Outposts keep their own secret. A forged conversation context in a call's metadata now needs the secret first.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [ ] The hosting library installs the gate once, and every row of the MCP server table has it
- [ ] A call without the secret, or with an unset one, is refused on each transport
- [ ] The agent presents the secret on every configured endpoint, and channel connections still connect, reconnect and deliver
- [ ] Dynamic outpost endpoints keep their own secret, unchanged
- [ ] The integration, E2E, eval and launch-stack setups all present the secret, and their suites stay green
- [ ] The MCP hosting rule documents the gate
