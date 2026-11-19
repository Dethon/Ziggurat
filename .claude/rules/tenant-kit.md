---
paths:
  - "TenantKit/docker-compose.yml"
  - "TenantKit/docker-compose.override.smoke.yml"
  - "TenantKit/.env"
  - "TenantKit/README.md"
  - "Domain/DTOs/Tunnel/HouseholdServerNames.cs"
  - "Tests/Integration/Tunnels/LemonadeForwarderTests.cs"
  - "LemonadeForwarder/**"
---

# The tenant kit

See also: `tunnel.md` (what `MCP__TUNNEL__*` and back-calls mean), `own-servers.md`, `tunnel-forwarding.md`.

## The tenant kit

`TenantKit/docker-compose.yml` is a household's own servers, run at its own site with the images the
deployment builds; `TenantKit/README.md` is how to run it and how the home moved into it. What it holds
to (`TenantKitConformanceTests`, through `DeploymentFiles.KitCompose`):

- **Each server is tunnelled by its environment alone**: `MCP__TUNNEL__HUB` (one value,
  `wss://${DEPLOYMENT_HOST}/tunnel`), `MCP__TUNNEL__CREDENTIAL` (`${TUNNEL_CREDENTIAL_<SERVER>}`, one per
  server) and `MCP__TUNNEL__SERVERNAME`. None is told a dialled server's gate — no `MCP__SHAREDSECRET`,
  `MCP__AUDIENCE` or `MCP__AGENTKEYSURL`. The kit's `.env` shares no line with the deployment's but
  what any site has of its own (paths, ids, the LAN address), the household's own OpenRouter key and
  `LOGINS__STATEKEY` (which the deployment keeps only for the household migration).
- **The names**: `websearch`, `vault`, `library`, `sandbox`, `printer`, `homeassistant`, `voice` —
  exactly the set the shipped definitions and channels name (a name nothing runs is a server offline
  forever: a context line and nothing louder), web search and voice under `HouseholdServerNames`. Each
  is also a server the test stacks host in its place (`AgentAppSettingsTests`, by
  `McpServerRegistrations`: tools for an agent, a channel for a channel).
  - One more runs only under the `lemonade` profile and is named by no agent: **`lemonade`**, the
    household's Lemonade forwarder (`LemonadeForwarder/`, service `lemonade-forwarder`). It serves no
    MCP (`AddTunnelledHost`: the tunnel names only its listeners, so the `mcp` tag reaches nothing) and
    makes no back-call. Its one listener, tag `chat` (`TunnelProtocol.ChatTag` — not voice's `lemonade`
    back-call, which goes the other way to the deployment's speech box), passes every request to
    `BOX__URL` (the kit's `LEMONADE_URL`; empty refuses its start) adding `BOX__APIKEY`, so the box's
    key never leaves the house. The agent reaches it through `TunnelRegistry.StreamsTo`
    (`LemonadeForwarderTests`).
- **Back-calls are loopback ports dialled by their client**: voice's `accounts` (`ACCOUNTS__BASEURL`),
  `topics` (`AGENT__BASEURL`), `lemonade` (`Stt__OpenAi__BaseUrl`/`Tts__OpenAi__BaseUrl`, `/v1`) and
  `metrics` (`METRICS__URL`, so voice opens no Redis); every other server's `metrics` for its heartbeat
  and its helpers' (`.claude/rules/observability.md`); the sandbox's `vfs-bridge` (`VFSBRIDGEURL`). A
  server names only the back-calls it makes, and every loopback URL it is given is one.
- **The deployment is reached through the tunnels alone**: every address a kit service is told is a kit
  service, its own loopback, the host's gateway (Music Assistant) or the deployment's main host (the
  hub, and web search's `HANDOFF__PUBLICBASEURL`); no network is `external`. The sandbox is on a
  network no other kit service is on and is handed nothing but its tunnel credential.
- **Its own state is its own**: the kit's Redis (`KIT_REDIS_PASSWORD`) holds web search's logins and
  links and the library's routing. Home Assistant's server and voice talk on the kit's network under the
  kit's own `Announce__Token`; the deployment's timers server rings voice through the relay, which
  presents voice's tunnel credential instead.
- **The state move** (`state-move`, profile `move`, `.claude/rules/household-migration.md`) copies the
  home's Redis state from the deployment's into the kit's, told the deployment's Redis on its own
  command line.
