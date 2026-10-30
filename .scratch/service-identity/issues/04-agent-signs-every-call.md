# 04: The agent signs every call; servers refuse unsigned ones

**What to build:** Holding a server's credential is no longer enough to act through it. The agent generates its own key, publishes its JWKS internally, and carries a call token for the one server in every call's `_meta`, keeping one per server per turn and minting a fresh one before it nears expiry; a worker's calls carry its parent turn's identity. The hosting library's gate refuses a call whose token is missing, expired, aimed at another server or not the agent's, with a reason, and reports it on the errors feed; the one accessor servers read who a call acts for through now answers the verified token's identity. The test and eval stacks run with the test key. Spec: `../spec.md` ("Lifetimes and long turns", "Servers").

**Blocked by:** 01, 02

**Status:** ready-for-agent

- [ ] A real server refuses an unsigned, expired, wrong-audience and foreign-key call, each with its reason, and admits a person's and a guest's
- [ ] A server acts for the person the verified token names and ignores a different principal written in `_meta`
- [ ] A turn running past the first token's lifetime keeps presenting valid tokens, under a fake clock
- [ ] A worker's calls act for its parent turn's person
- [ ] The integration, E2E and eval stacks pass with the test key
