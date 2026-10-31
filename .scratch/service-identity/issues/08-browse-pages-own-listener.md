# 08: The browse server's public pages get their own listener

**What to build:** No routing mistake can expose the browse server's `/mcp`: the login handoff's live view and the logins page move to a second listener, the proxy routes `/browse/*` to it, and `/mcp` stays on the internal listener alone. Spec: `../spec.md` ("The browse server").

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The public pages answer on the second listener and not on the internal one, and `/mcp` the reverse
- [x] The conformance check asserts the proxy routes `/browse/*` to the second listener's port
- [x] The login handoff tests pass through the new listener
