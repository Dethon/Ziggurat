---
paths:
  - "Mcp.Hosting/**"
  - "McpServer*/**"
  - "McpChannel*/**"
  - "Tests/Integration/McpServers/**"
---

# MCP Server Hosting

`Mcp.Hosting` holds what being an MCP server means, so no server hand-writes it. The project
references Domain, the MCP server package and the configuration binder alone, never Infrastructure —
the ServiceBus channel server depends on Domain only and must stay that way. A server that needs an
Infrastructure adapter takes the reference itself and says why (Telegram dictates, so it holds the
shared transcription client); `Mcp.Hosting` must never make that choice on a server's behalf.

- **`IConfigurationBuilder.BindSettings<TSettings>()` is the only way a server reads configuration.**
  A server may wrap it in a one-line helper (voice's `ConfigModule.GetVoiceSettings()`), but the
  wrapper must delegate to `BindSettings` — the invariant is the binder, not the call site.
  Environment variables first, user secrets last, so **user secrets win** — deliberately, and the
  reverse of the framework default. Read `docs/adr/0005-user-secrets-outrank-environment-variables.md`
  before touching the order; reversing it silently switches off CapSolver, web push and the Music
  Assistant action on every containerised deployment. The secrets id comes off the entry assembly, so
  the five servers with no `UserSecretsId` simply have no such source. The **outpost** is the one
  server that adds a source of its own on top: it is configured by the flags its operator types, and
  the host puts the command line *underneath* the environment, so `OutpostConfiguration` re-applies
  it above `BindSettings`' sources — a `Jailed` variable that happens to be set must not overrule a
  `--jailed` somebody typed. `McpServerTableTests` names that exemption by file; nothing else may
  copy it. Nested sections bind through
  the plain call. A `required` member that bound to null fails startup naming it; **null only, never
  empty** — six shipped servers carry required members that ship as `""` and are filled from secrets
  (ServiceBus, Telegram, WebSearch, HomeAssistant, Idealista, Library).
- **`IServiceCollection.AddMcpHost(settings)`** is the four things every server has: the settings
  singleton, the server, the HTTP transport and the gate on `/mcp`. All fourteen use it.
- **Every `/mcp` asks for a secret, and the host installs the gate, not the server.** A tool
  believes whatever `ConversationContext` a call's `_meta` claims, so a call must present
  `Authorization: Bearer <secret>` first. `AddMcpHost` constrains its settings to
  `IMcpHostSettings` — a server cannot be hosted without saying which secret guards it — and
  registers `McpSecretGate`, an `IStartupFilter` that answers 401 ahead of routing, comparing with
  `Domain/Security/SharedSecret.cs` (the one comparison every shared-secret gate uses). A deployment
  server's settings carry `McpGateSettings Mcp`, bound from `MCP__SHAREDSECRET` — one deployment-wide
  value, which the agent binds under the same name and presents on every endpoint the deployment's
  own settings name (`AgentSpecProjection`, agents and workers alike; `DeploymentEndpoints` is the
  list) and on every channel connection. A registered agent's endpoints are whatever
  `POST /api/agents` said, so one the settings do not name is dialled bare — the secret opens every
  server, and must not follow a registration to an address of its choosing. The **outpost**
  answers the member with its own `SharedSecret` and never sees the deployment's. **The gate guards
  the `/mcp` path and nothing else**: a server's other endpoints are called by browsers, Home
  Assistant and satellites, and the ones that need a gate carry their own token, so every server
  must map its endpoint at exactly `"/mcp"` (`McpServerTableTests` reads it off the source). An
  unset secret refuses every call. The SDK's legacy SSE endpoints are off, so streamable HTTP is the
  one transport; `McpSecretGateTests` pins all three verbs, and a test that boots a server through
  the hosting library presents `Tests/McpTestSecret`.
- **`AddToolServer(settings, errorResult?)`** is the host plus the call-tool error filter, for the
  nine servers that offer the agent things to call. Being a tool server and being a channel server
  are independent, so a dual-role server calls `AddToolServer` and then `AddChannelServer`.
- **The error filter is one shared registration, installed at most once.** A cancelled call
  propagates as the abort it is; anything else is logged and becomes the caller's error result.
  **Who is calling travels as data, never as an ambient**: a tool reads the call's
  `ConversationContext` off its `_meta` (`ConversationScope.Parse`) and hands on what it needs, and
  a filesystem backend — whose operations never see the request — is asked by the registrar for
  itself as that caller sees it (`FileSystemBackendBase.For(FileSystemCaller)`, per call: the
  conversation context and, on an exec the agent minted one for, the exec bridge's call token —
  `HaFileSystem` reads the first, `SandboxFileSystem` the second). Absent means the call carried
  none, and a consumer refuses rather than guesses. There is no `AsyncLocal` on this path; don't add one to save a parameter. The context
  also carries `ConfigPatchModel`, the model the turn asked for. **The Jev client holds the local-box rule and the bill, and every use feeds it explicitly**:
  `JudgmentRequest.Caller` (a `JudgmentCaller`: the turn's model, the sender, the agent and the
  conversation) is a required field. `TypeSafeJudge` sends nothing for a `lemonade/` turn model and
  answers `AbsenceReason.LocalTurn`, so a use cannot be written without saying which turn it asks
  for, and none checks for itself — it only decides what that absence means to it (not a miss to
  count). Every answered judgment publishes a `TokenUsageEvent` billed to the caller, as a chat turn
  does, so Jev's cost lands on the dashboard's token totals with every other model's. No use
  publishes a usage event of its own; a use's own event (`SkillPreloadEvent`, `ExecScreenEvent`)
  may keep a copy of the cost for its dashboard family, and the eval's spend ignores that copy. The value is passed down as plain data, never read from an ambient: a
  server's tool parses it from the call's `_meta` (`JudgmentCaller.For(ConversationScope.Parse(…))`)
  and hands it on as a parameter, the agent host takes it from the message, and a question with no
  turn behind it (memory) names the user with a null turn model. Two
  filters nested around each other would let the outer one convert the very cancellation the inner
  rethrows, so a second ask is a no-op and the first ask's error shape wins.
- **`Tests/Integration/McpServers/McpServerRegistrations.cs` is the one server table.** Fourteen
  rows, each driving the real `ConfigModule`; `McpServerContractTests` asserts every server resolves
  its settings as a singleton, registered the host, has exactly one call-tool filter and exactly one
  `/mcp` gate holding the secret its settings carry. A new server is one new row.
