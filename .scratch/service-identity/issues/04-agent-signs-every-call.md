# 04: The agent signs every call; servers refuse unsigned ones

**What to build:** Holding a server's credential is no longer enough to act through it. The agent generates its own key, publishes its JWKS internally, and carries a call token for the one server in every call's `_meta`, keeping one per server per turn and minting a fresh one before it nears expiry; a worker's calls carry its parent turn's identity. The hosting library's gate refuses a call whose token is missing, expired, aimed at another server or not the agent's, with a reason, and reports it on the errors feed; the one accessor servers read who a call acts for through now answers the verified token's identity. The test and eval stacks run with the test key. Spec: `../spec.md` ("Lifetimes and long turns", "Servers").

**Blocked by:** 01, 02

**Status:** done

- [x] A real server refuses an unsigned, expired, wrong-audience and foreign-key call, each with its reason, and admits a person's and a guest's (`CallTokenGateTests`)
- [x] A server acts for the person the verified token names and ignores a different principal written in `_meta` (`CallTokenGateTests`)
- [x] A turn running past the first token's lifetime keeps presenting valid tokens, under a fake clock (`McpAgentCallTokenTests`)
- [x] A worker's calls act for its parent turn's person (`McpAgentCallTokenTests`)
- [x] The integration, E2E and eval stacks pass with the test key — integration and unit suites, the WebChat reply E2E and the eval harness's unarmed tests; no armed eval was run

Notes for later tickets:
- A call that acts for no one is signed as nobody (`IdentityClaims.Nobody`, `sub` = `nobody`, no household), an amendment to ticket 01's claim set: the agent's channel plumbing and a fire nobody vouched for still carry a token. 05 must refuse an assertion that names nobody.
- 05 plugs the verified assertion in at `Infrastructure/Agents/Mcp/CallIdentity.cs`, the one method that says who a turn's calls are signed for (today: the principal's account, else nobody — the voice guest is nobody until 05, since the agent cannot know a guest's household).
- A server's `Principal` for a person is now `Principal(chn, accountId, Account)`: the channel's own name for them is not signed.
- There is no rotation command for the agent's key yet.
