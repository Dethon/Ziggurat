# 05: Offline servers

**What to build:** A conversation's tool sessions are reconciled at each turn's start: a household's server that came online joins, one that went offline leaves, and one line of the turn's context names the household's servers that are offline. A call in flight when its tunnel drops fails with an error saying the server went offline. This deliberately changes ADR 0027 for tunnel endpoints only (a configured one that is offline is left out, not fatal); shared servers' URL endpoints keep failing as before. An operator command lists a household's connected servers, since when, and the tools each offers. Spec: `../spec.md` ("Channels and turns").

**Blocked by:** 03

**Status:** done

- [x] A turn built while a server is offline offers none of its tools and names it in its context
- [x] A server that comes online is offered on the next turn of the same conversation
- [x] A call in flight when the tunnel drops fails with the went-offline error
- [x] The operator command lists connected servers with their connection time and tools

ADR 0027's exception for tunnel endpoints is recorded in `.claude/rules/tunnel.md` ("Offline servers"),
not in the ADR. The command is `list-household-servers --household <tenantId>`, reading the running
agent's `/internal/tunnels/{tenantId}` behind `AGENTAPI__SHAREDSECRET`.
