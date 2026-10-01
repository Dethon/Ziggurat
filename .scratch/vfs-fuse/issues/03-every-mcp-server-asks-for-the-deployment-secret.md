# 03 — Every MCP server asks for the deployment secret

**What to build:** Every deployment MCP server, channel servers included, refuses a call to `/mcp` that does not present one deployment-wide secret. The agent reads the secret once from one setting and presents it on every configured endpoint, through the route outposts already use. Outposts keep their own secret. A forged conversation context in a call's metadata now needs the secret first.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [x] The hosting library installs the gate once, and every row of the MCP server table has it
- [x] A call without the secret, or with an unset one, is refused on each transport
- [x] The agent presents the secret on every configured endpoint, and channel connections still connect, reconnect and deliver
- [x] Dynamic outpost endpoints keep their own secret, unchanged
- [ ] The integration, E2E, eval and launch-stack setups all present the secret, and their suites stay green
- [x] The MCP hosting rule documents the gate

## Comments

Implemented. The setting is `Mcp:SharedSecret` on both ends — `MCP__SHAREDSECRET`, one placeholder in `DockerCompose/.env`, wired explicitly into the 13 `mcp-*` services and the agent in `docker-compose.yml`.

- **Gate:** `AddMcpHost` now requires `TSettings : IMcpHostSettings` (`McpGateSettings Mcp`) and registers `McpSecretGate`, an `IStartupFilter` that answers 401 on `/mcp` (path-scoped, ahead of routing) unless the call carries `Authorization: Bearer <secret>`; the comparison is `Domain/Security/SharedSecret.cs`. Every deployment settings record gained `public McpGateSettings Mcp`. The outpost answers the member with its own `SharedSecret`, so its hand-written `app.Use` gate in `McpServerOutpost/Program.cs` was removed as a duplicate of the host's (same secret, same rule, `/mcp` is its only route).
- **Agent:** `AgentSettings.Mcp` is registered in DI; `MultiAgentFactory` reads it once and `AgentSpecProjection` applies it to every configured endpoint of agents and workers (`McpServerEndpoint.Configured(address, secret)`); dynamic outpost endpoints keep `OutpostAccess.SharedSecret`. `McpChannelConnection` takes `mcpSecret` and presents it on every dial, reconnects included.
- **Transports:** legacy SSE is off in SDK 2.2.0 and no server enables it, so streamable HTTP is the one transport; `McpSecretGateTests` refuses POST/GET/DELETE without, with a wrong, and with an unset secret (and `/mcp/sse`, `/mcp/message`).
- **Not run here:** the E2E suites (`WebChatStack`, `SandboxE2EFixture`) and the eval (`EvalStack`, `EvalSandbox`) were updated to present the secret and compile, but were not run — hence the one unticked box. `JonasMcpStackFixture` (no consumer in the suite) was updated the same way. Local setups (`scripts/run-local.sh`, `.vscode/launch.json`, `.zed/debug.json`) give every server and the agent `MCP__SHAREDSECRET` (default `local-mcp-secret`).
