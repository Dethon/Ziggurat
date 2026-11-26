---
paths:
  - "Mcp.Hosting/**"
  - "McpServer*/**"
  - "McpChannel*/**"
  - "Tests/Integration/McpServers/**"
  - "Domain/Security/ServerAddress.cs"
---

# MCP Server Hosting

`Mcp.Hosting` holds what being an MCP server means, so no server hand-writes it. It references Domain,
the MCP server package and the configuration binder alone, **never Infrastructure** — that would hand
every server Infrastructure's whole stack. A server needing an Infrastructure adapter takes the
reference itself and says why (Telegram, for transcription); `Mcp.Hosting` never makes that choice for it.

- **`IConfigurationBuilder.BindSettings<TSettings>()` is the only way a server reads configuration.**
  A one-line wrapper (voice's `ConfigModule.GetVoiceSettings()`) must delegate to it. **User secrets
  win** over environment variables — deliberately, the reverse of the framework default; read
  `docs/adr/0005` before touching the order (reversing it silently switches off CapSolver, web push and
  the Music Assistant action on every containerised deployment). The **outpost** alone adds a source on
  top: `OutpostFlags` re-applies its command line above `BindSettings`' sources, so a stray `Jailed`
  variable never overrules a typed `--jailed`; `McpServerTableTests` names that exemption by file and
  nothing else may copy it. A `required` member that bound to null fails startup naming it — **null
  only, never empty** (several servers ship required members as `""`, filled from secrets).
- **`IServiceCollection.AddMcpHost(settings)`** is the five things every server has: the settings
  singleton, the server, the HTTP transport, the gate on `/mcp` and the call-token gate on every tool
  call (`CallTokenGate`; every server checks, the outpost too, against the keys its tunnel delivers).
  Every server uses it. The credential lets a caller talk to a server; only the agent's call token says
  who a tool call acts for (`.claude/rules/service-identity.md`, "Call tokens").
- **Every `/mcp` asks for a secret, and the host installs the gate, not the server.** A tool believes
  the `ConversationContext` a call's `_meta` claims, so a call must first present
  `Authorization: Bearer <secret>`. `AddMcpHost` constrains settings to `IMcpHostSettings` and registers
  `McpSecretGate`, an `IStartupFilter` (401 ahead of routing, compared by `Domain/Security/SharedSecret.cs`, the one comparison every
  shared-secret gate uses). A deployment server binds `McpGateSettings Mcp` from `MCP__SHAREDSECRET`,
  its own compose variable (`MCP_SECRET_<SERVER>`), so holding one opens no other. A server given a
  tunnel (`Mcp:Tunnel`, `.claude/rules/tunnel.md`) — every household server, the kit's sandbox among
  them — asks for its tunnel credential instead, which the relay presents: **no deployment holds a
  household server's secret**. The agent binds each server's under `Mcp:Secrets` keyed by the name it
  answers to (`MCP__SECRETS__<host>`, the same variable on both ends). `McpCredentials` gives a credential
  only to an endpoint the deployment's own settings name (`DeploymentEndpoints`; via `AgentSpecProjection`,
  for agents and workers alike, and for channel connections); a configured host with no credential stops
  the agent at startup. A registered agent's endpoints (whatever `POST /api/agents` said) that the settings
  do not name are dialled bare — **no credential may follow a registration to an address of its
  choosing**. The outpost's `OutpostSettings.Mcp` is
  computed from its flags and `CREDENTIAL`, so it never sees the deployment's. **The gate guards the
  `/mcp` path and nothing else**, so every server maps its endpoint at exactly `"/mcp"`
  (`McpServerTableTests`). A server's other endpoints (browsers, Home Assistant, satellites) hold no
  deployment secret, so one that needs a gate carries its own token. An unset secret refuses every call. Streamable HTTP is the one transport
  (legacy SSE off). `McpSecretGateTests`, `CallTokenGateTests`; a test booting a server through the
  library presents `Tests/McpTestSecret`.
- **A server is keyed by its `ServerAddress` and never by a URL's host** (`Domain/Security/ServerAddress.cs`).
  `Name` is the call token's audience (`TokenAudiences.CallTo`), the key `McpCredentials` finds its
  credential by and what the server binds as `MCP__AUDIENCE` (`CallTokenGate`, via
  `ServerAddress.Named`); `Qualifier` qualifies its tools (`mcp__<qualifier>__<tool>`; the host, or
  `host-port` off the scheme's default port, so `http://mcp-vault:8080` is `mcp-vault` to its token and
  credential and `mcp-vault-8080` to its tools); `CacheKey` caches its prompts and skills (a household server's
  full name, so one household's prompts never serve another); the address itself, equal as a server,
  is what `DeploymentEndpoints` and `McpClientManager.DialledServers` compare. URLs go through
  `ServerAddress.FromUrl` (http(s) with a host only); a household's tunnelled server is
  `ServerAddress.Tunnelled(household, name)` (`.claude/rules/own-servers.md`).
  `ServerAddressConformanceTests` fails on any other `Agent`, `Infrastructure`, `Domain` or
  `Mcp.Hosting` file reading a `.Host` (web sites — browser, logins, search, push — are excluded by path).
- **A server whose pages the proxy routes keeps them on a listener of their own.** The browse server's
  login handoff and logins pages answer on its second listener (8081), `/mcp` on the first (8080),
  both named by its `Listeners` settings and bound in `Program.cs` with `ListenAnyIP` (its image clears
  `ASPNETCORE_HTTP_PORTS`). `PagesListener` keys on the arrival socket — **never `RequireHost`**, whose
  Host header the proxy passes through from the browser — answering 404 outside `/browse` on the pages
  listener and for `/browse` on the other; inserted first, ahead of the gate, so `/mcp` is absent there.
  The proxy reaches the pages through the relay alone, by the `pages` tag the server registers
  (`.claude/rules/tunnel-forwarding.md`, "Browse pages"); no route names the server
  (`DeploymentConformanceTests`). `WebIdentityServerHarness` opens both listeners. The apps server's
  app origins are the second instance (`AppPagesListener`, `.claude/rules/apps.md`).
- **`AddToolServer(settings, errorResult?)`** is the host plus the call-tool error filter, for servers
  that offer the agent things to call. Tool server and channel server are independent: a dual-role
  server calls `AddToolServer` then `AddChannelServer`.
- **A channel server that waits on its turns uses `Mcp.Hosting`'s pieces** (Sugary's estimates), written for none of them: `AddAgentTurns` (the table and its `send_reply`/refusing `request_approval`), `AgentTurns.SendAsync` (sent under a minted `TurnKey`, the last message's text is the answer, cancelled on the caller's deadline or abort, the cancel naming the agent the message named since the agent groups by conversation and agent; the clock is the caller's), `OpenConversations<TOwner, TCarried>` (the follow-up binding, keyed by an owner the server chooses, ids spelled as a `ConversationIdentity`), and `AddUploadStore` (`UploadStore`, its sweeper and `fetch_attachment`). A file saved for a household (`SaveForHouseholdAsync`) is handed only to a fetch whose call token names that household; one saved for none, Sugary's (its fetches act for nobody), to any, and is laid out byte for byte as before.
- **The error filter is one shared registration, installed at most once** (`CallToolErrorFilter`): a
  cancelled call propagates as the abort it is; anything else is logged, reported as a `ToolCallFailed`
  `ErrorEvent` under the verified call's household (`.claude/rules/observability.md`, "Households"), and
  becomes the caller's error result. A second ask is a no-op — nested filters would let the outer one
  convert the cancellation the inner rethrows — so the first ask's error shape wins.
- **Who is calling travels as data, never as an ambient.** A tool reads the call's
  `ConversationContext` through the one accessor, `Mcp.Hosting/CallTurn.cs` (`call.Turn`,
  `call.TryScope`); its principal is the identity the call token verified (`call.Identity`), never the
  one `_meta` claims. The household is `call.Household` (`Mcp.Hosting/CallHousehold.cs`), proven to the
  accounts service with the verified token, so a tool reads its own section of that household's
  configuration and no other's (`.claude/rules/household-config.md`, "A shared server's accessor").
  `call.VerifiedToken` is the token itself, handed back to the agent alone (the apps mount's checks;
  `service-identity.md`, "Call tokens"). No
  server parses the raw `_meta` for the turn or the token (`CallTurnConformanceTests`; the token is read
  in `CallTokenGate.cs` alone); the fs_* registrar in `Infrastructure` references `Mcp.Hosting` for that
  alone. A filesystem backend gets the caller through `FileSystemBackendBase.For(FileSystemCaller)` per
  call (the context, and on a bridged exec its call token). Absent means the call carried none, and a
  consumer refuses rather than guesses. There is no `AsyncLocal` on this path; don't add one to save a
  parameter.
- **The Jev client holds the local-box rule and the bill, and every use feeds it explicitly**:
  `JudgmentRequest.Caller` (a `JudgmentCaller`: the turn's model, the sender, the agent, the
  conversation) is required; the call's context carries `ConfigPatchModel`, the model the turn asked for. `TypeSafeJudge` sends nothing for a `lemonade/` turn model and answers
  `AbsenceReason.LocalTurn`; no use checks for itself, it only decides what that absence means to it
  (not a miss). Every answered judgment publishes a `TokenUsageEvent` billed to the caller; no use
  publishes one of its own (a use's own event, `SkillPreloadEvent` or `ExecScreenEvent`, may keep a copy
  of the cost, which the eval's spend ignores). The caller is passed down as data: a server's tool takes
  `JudgmentCaller.For(context.Turn)` and hands it on, the agent host takes it from the message with the
  household's key (`JudgmentCaller.Payer`), a server's caller names the household and asks on its own
  key, and a question with no turn (memory) names the user with a null turn model.
- **`Tests/Integration/McpServers/McpServerRegistrations.cs` is the one server table**: one row per
  server, each driving the real `ConfigModule`; `McpServerContractTests` asserts each resolves its
  settings as a singleton, registers the host, has exactly one call-tool filter and exactly one `/mcp`
  gate holding its settings' secret. A new server is one new row.
