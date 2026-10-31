# 03: A server's tools through its tunnel

**What to build:** The tracer bullet. A server built on the hosting library, given a hub and a tunnel credential, opens one outbound WebSocket through the proxy's new tunnel route to a relay in the agent and serves its MCP traffic over it, reconnecting with backoff. The relay verifies the credential with the accounts service and answers with the household, the server's name, its synthetic address and the agent's public keys (again on rotation); the server's call-token check uses those keys. Agent definitions name a household's server as a tunnel endpoint resolved against the turn's household, reached through an HTTP handler over the relay. A refused tunnel is closed with a reason, logged and reported; revoking closes an open one. Each opening of a tunnel is a new connection generation in the sense of ADR 0012. The eval and integration stacks resolve tunnel endpoints to in-process servers. Spec: `../spec.md` ("The tunnel", "Credentials").

**Blocked by:** 01, 02

**Status:** ready-for-agent

- [ ] A real server connected to a real relay answers a signed tool call through the tunnel and refuses an unsigned one
- [ ] An unknown, revoked or other-name credential is refused with its reason and reported; revoking closes an open tunnel
- [ ] The tunnel reconnects after the relay restarts, and a key rotation reaches the server over the tunnel
- [ ] The proxy routes only the tunnel's path to the relay (conformance)
- [ ] The eval and integration stacks pass with tunnel endpoints resolved in process
