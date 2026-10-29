# 06: Every MCP server has its own credential

**What to build:** Holding one MCP server's credential opens no other server. Each server and channel server is fed its own credential; the agent holds a map from endpoint host to credential and presents each endpoint and channel connection its own; an endpoint the deployment doesn't name is dialled with none; a configured host without a credential stops the agent at startup. The sandbox holds only its own. The hosting rules and the placeholders' comments follow. Spec: `../spec.md` ("Per-server MCP credentials").

**Blocked by:** 01

**Status:** done

- [x] The agent spec projection hands each endpoint its own host's credential, and channel connections likewise
- [x] A real server refuses another server's credential
- [x] The agent refuses to start when a configured host has no credential
- [x] The conformance check asserts no two servers share a credential variable and the sandbox names only its own
- [x] The chat-client E2E stack wires one credential per server; the jonas integration fixture and the eval's stack run their servers on loopback, where the agent's credentials are keyed by host, so they share one value. All their suites pass
