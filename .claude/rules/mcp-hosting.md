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
  the five servers with no `UserSecretsId` simply have no such source. Nested sections bind through
  the plain call. A `required` member that bound to null fails startup naming it; **null only, never
  empty** — six shipped servers carry required members that ship as `""` and are filled from secrets
  (ServiceBus, Telegram, WebSearch, HomeAssistant, Idealista, Library).
- **`IServiceCollection.AddMcpHost(settings)`** is the three things every server has: the settings
  singleton, the server and the HTTP transport. All thirteen use it.
- **`AddToolServer(settings, errorResult?)`** is the host plus the call-tool error filter, for the
  nine servers that offer the agent things to call. Being a tool server and being a channel server
  are independent, so a dual-role server calls `AddToolServer` and then `AddChannelServer`.
- **The error filter is one shared registration, installed at most once.** A cancelled call
  propagates as the abort it is; anything else is logged and becomes the caller's error result. Two
  filters nested around each other would let the outer one convert the very cancellation the inner
  rethrows, so a second ask is a no-op and the first ask's error shape wins.
- **`Tests/Integration/McpServers/McpServerRegistrations.cs` is the one server table.** Thirteen
  rows, each driving the real `ConfigModule`; `McpServerContractTests` asserts every server resolves
  its settings as a singleton, registered the host and has exactly one call-tool filter. A new server
  is one new row.
