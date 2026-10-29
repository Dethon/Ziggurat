# 05: Every MCP server has its own credential

**What to build:** One secret no longer opens every server. Each server and channel server keeps reading `MCP__SHAREDSECRET`, fed from its own variable (`MCP_SECRET_VAULT`, `MCP_SECRET_SANDBOX`, …; placeholders in `.env`). The agent's `Mcp:SharedSecret` becomes `Mcp:Secrets`, a map from endpoint host to secret; the agent presents each configured endpoint and channel connection the secret for its host, and a host it doesn't name is dialled with none, as today. A configured host with no secret fails startup. The sandbox holds only its own. `mcp-hosting.md` and the `.env` comments follow. Spec: `../spec.md` ("Per-server MCP credentials").

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] `AgentSpecProjection` hands each endpoint its host's secret, and channel connections likewise (unit)
- [ ] A real server host refuses another server's secret (prior art: the `Mcp.Hosting` gate tests)
- [ ] The agent refuses to start when a configured deployment host has no secret
- [ ] The compose conformance test asserts no two servers share a secret variable and the sandbox names only its own
- [ ] The E2E, integration and eval stacks wire per-server secrets and their suites pass
