---
paths:
  - "Mcp.Hosting/Tunnel/**"
  - "Mcp.Hosting/ToolServerExtensions.cs"
  - "Infrastructure/Tunnels/Tunnel*.cs"
  - "Infrastructure/Tunnels/OpenTunnel.cs"
  - "Infrastructure/Tunnels/RelayHandler.cs"
  - "Infrastructure/Tunnels/CurrentTunnelStreams.cs"
  - "Infrastructure/Tunnels/RelayedOwnServers.cs"
  - "Infrastructure/Tunnels/BackCall*.cs"
  - "Infrastructure/Clients/Accounts/TunnelCredentialClient.cs"
  - "Infrastructure/Metrics/HttpMetricSink.cs"
  - "Agent/App/TunnelApi.cs"
  - "Agent/Modules/TunnelModule.cs"
  - "Domain/DTOs/Tunnel/TunnelProtocol.cs"
  - "Domain/DTOs/Tunnel/TunnelSettings.cs"
  - "Domain/DTOs/Tunnel/TunnelCredentials.cs"
  - "Domain/DTOs/Tunnel/BackCallProtocol.cs"
  - "Domain/DTOs/Tunnel/HouseholdServerNames.cs"
  - "Domain/tunnel.json"
  - "DockerCompose/docker-compose.yml"
  - "DockerCompose/.env"
  - "DockerCompose/caddy/Caddyfile"
  - "Tests/Integration/Tunnels/**"
---

# The tunnel

See also: `own-servers.md` (turns, offline, channels, key commands), `tunnel-forwarding.md` (browse pages, relayed calls), `tenant-kit.md`.

A household's **own server** (CONTEXT.md, "Households") is never dialled. It opens one outbound
WebSocket — its **tunnel** — to the **relay** in the agent, through the proxy's one route for it
(`/tunnel`), and everything the agent says to it travels inside. Spec:
`.scratch/tenants-tunnel/spec.md`.

## The wire

`Mcp.Hosting/Tunnel/TunnelMux.cs` is the whole protocol, used at both ends: **a stream multiplexer
over one WebSocket, and nothing more** (it knows nothing of MCP, pages or upgrades).

- **Binary messages** are stream frames (`Open`, `Data`, `Close`, `Reset`; layout and size limits in
  `TunnelMux`'s header and constants).
- **Text messages** are control messages (`TunnelControl`, `TunnelProtocol.cs`): `admitted` and `keys`,
  relay to server, delivered **in order among the frames**, so a key arrives before the call signed with it.
- **Either end opens streams**: relay odd, server even. Server-opened streams are **back-calls**,
  reset before admission.
- **A close from either end ends a stream in both directions**; a reset or the tunnel's end makes the
  next read throw `IOException`.
- **What arrives waits until it is read**, up to 64 frames per stream, then the reader stops for the
  whole tunnel: a stream nobody reads holds up its own tunnel and no other.

Tested at the round trip (`TunnelRelayTests`), never frame by frame.

### A stream is a TCP connection to one of the server's own listeners

`TunnelClient` turns every relay-opened stream into a TCP connection to `127.0.0.1:<port>` of the
listener its tag names, so **no gate is rewritten and no request is handed to an in-process handler**:
`McpSecretGate`, `CallTokenGate` and `PagesListener` see a request exactly as if dialled. The `mcp` tag
(`TunnelProtocol.McpTag`) is the first bound address unless registered otherwise; a second listener is
named with `services.AddTunnelListener(tag, port)`.

## Opening a tunnel

The server connects to `Mcp:Tunnel:Hub` (`wss://<main host>/tunnel`) with `Authorization: Bearer
<tunnel credential>` and `X-Ziggurat-Tunnel-Server: <name>` on the upgrade — **never in the URL**,
which the proxy logs. The relay (`TunnelRelay`):

- **Asks the accounts service on every open** (`TunnelCredentialClient`, `POST
  /accounts/internal/tunnels/verify`, as `CALLERS__agent`; contract in tunnel-credentials.md). Only a 404
  is a refusal; any other failure is the service's, never reported as the credential's.
- **Refused** (`Unknown`, `Revoked`, `AnotherServer`, `Suspended`): closed `PolicyViolation` with the
  reason, published as `ErrorEvent` (`ErrorType` `TunnelRefused`) — **never the credential**; a known
  credential's refusal is filed under its household (`TunnelCredentialRefused.TenantId`).
- **Unverified** (accounts did not answer): closed `InternalServerError`, `Unverified`; not a refusal.
- **Admitted**: sent `TunnelAdmitted(TenantId, ServerName, Audience, Keys)` — household and name from
  the **verified credential**, never the claim; audience `ServerAddress.Tunnelled(household, name).Name`.

How a tunnel ends (the close description the server reads): a refusal's reason; `Replaced` (a newer
opening of the same server); `Revoked` and `Suspended` (`TunnelRevocationListener` on
`accounts:tunnel-credentials:revoked` and `accounts:households:suspended` — a suspension closes every
tunnel of the household, outposts included — the tunnel removed from the registry before its close is sent; a suspended household's servers return by themselves once resumed);
`Restarting` (agent stopping, closed `EndpointUnavailable` so shutdown is not held open). Refusals, `Replaced`, `Revoked` and `Suspended`
all close with `PolicyViolation` (`TunnelRelay`, `TunnelRevocationListener`), which `TunnelClient` reads as
"wait the longest"; `Unverified` and `Restarting` do not, so those servers come back on the ordinary backoff.

## The server's end

`AddMcpHost` reads `McpGateSettings.Tunnel` (`TunnelSettings`): a server given all three of
`MCP__TUNNEL__HUB`, `MCP__TUNNEL__CREDENTIAL`, `MCP__TUNNEL__SERVERNAME` is tunnelled, otherwise dialled.
**No server writes tunnel code.** Tunnelled, the host:

- runs `TunnelClient`, reopening with backoff (`ReconnectAfter` doubling to `ReconnectAtMost`, reset
  once admitted); a `PolicyViolation` close waits the most, so two servers claiming one name do not flap.
- gates `/mcp` behind **the tunnel credential** instead of `Mcp:SharedSecret`, which the relay presents
  on every forwarded request (`RelayHandler`), so **no deployment holds a household server's secret** —
  the deployment's compose and `.env` gain nothing; the kit's `.env` holds one credential per server.
- checks call tokens against `TunnelTrust`: audience and keys handed over **this tunnel**, never
  `Mcp:Audience` or `Mcp:AgentKeysUrl`; none before admission (`KeysUnavailable`), and each `keys` message
  **replaces** the set (`TunnelTrust`), so the relay always sends the whole published set — which is how
  retirement reaches the server.

**The hub's certificate** is checked as any HTTPS client does. `MCP__TUNNEL__HUBCERTIFICATEAUTHORITY`
(a PEM root, read once at start) additionally trusts a chain to that root
(`X509ChainTrustMode.CustomRootTrust`); the hub's intermediates go into the build (`ExtraStore`), never
trusted by themselves; a name mismatch or no certificate is refused either way. Tested in
`TunnelRelayTests` and on the smoke stack.

The four timings (`KeepAlive`, `KeepAliveTimeout`, reconnect) are one shared policy file,
`Domain/tunnel.json` (`mcp.tunnel`), read by `BindSettings` and the agent's `GetSettings` before the
environment (`TunnelPolicyFileTests`).

## Keys

The relay sends the agent's published keys in `TunnelAdmitted`, then **before any exchange it
forwards, whenever `SigningKeyStore.PublishedKeys` differs from what it last sent** (`RelayHandler` →
`OpenTunnel.SendKeysIfChangedAsync`) — per exchange, not per pooled connection — so the server holds
them before reading the call; retirement goes the same way. No public route serves the agent's keys
to a household.

## The registry: one opening is one generation

`TunnelRegistry` holds open tunnels, **at most one per (household, server name)**. Each `OpenTunnel`
is one connection generation (ADR 0012): `Generation` process-wide and increasing, `Closed` a token,
`Client` an `HttpClient` whose every connection is a stream to the `mcp` listener, failing
`HttpRequestException("<name> went offline: its tunnel closed")` once gone.

- `Find(tenantId, serverName)`: an open tunnel or none (one closing is none, so no caller checks
  `IsOpen` again). `Open`: a snapshot.
- `WatchAsync(ct)`: every open tunnel as `TunnelOpened`, then each `TunnelOpened` /
  `TunnelClosed(tunnel, reason)`, nothing missed between; a replacement publishes the old
  `TunnelClosed(Replaced)` before the new `TunnelOpened`.
- `StreamsTo(tenantId, serverName, tag)`: an `HttpMessageHandler` for the agent's **own** requests to a
  household server's listener (the Lemonade forwarder's `chat`), presenting nothing. Every request asks
  `Find` first and drops a stale pool (`CurrentTunnelStreams`), so a revoked tunnel still carrying bytes
  is never ridden and another household's same-named server never is; none open fails `"<name> is
  offline: its tunnel is not open"`. Tested in `TunnelStreamsTests`.
- A server that reconnects **replaces** its old tunnel (closed `Replaced`). Nothing carries over between
  generations.

## Back-calls

What a household's server still needs of the deployment it asks for over its own tunnel, and only
that: a stream it opens with one of `BackCallProtocol`'s tags, carrying HTTP/1.1.

| Tag | From (server name) | Reaches | Paths (all POST) | For |
|---|---|---|---|---|
| `accounts` | `voice` | the accounts service (`Accounts:BaseUrl`) | `/accounts/internal/resolve`, `/accounts/internal/guest` | voice's speakers and guest |
| `accounts` | `websearch` | the accounts service | `/accounts/internal/household-config/brave-search` | the household's Brave key; neither server reaches the other's paths (`BackCallTargets`' grants) |
| `topics` | `voice` only | the agent's own `TopicMinting.Path` | `/internal/topics` | voice's topics |
| `lemonade` | `voice` only | the shared Lemonade (`backCalls:lemonadeUrl`) | `/v1/audio/transcriptions`, `/v1/audio/speech` | voice's STT and TTS |
| `vfs-bridge` | `sandbox` only | the agent's own exec bridge | under `/api/vfs-bridge/` | the sandbox's FUSE daemon |
| `metrics` | any but an outpost | the relay itself | `BackCallProtocol.MetricsPath` (`/events`) | any server's metric events; the sandbox's own heartbeat alone (`BackCallTargets.MayPublish`: its container runs the model's commands, which can dial the port) |

**A back-call is for the servers that make it**, by the verified credential's server name
(`HouseholdServerNames.Voice`/`.Sandbox`; an outpost is `outpost-<NAME>` and publishes no metrics).
The accounts back-call mints a voice assertion for anyone in the household, and topics are minted on
such assertions, so a printer, vault, library or outpost must not reach them; nor voice the bridge. A
new back-call, or a server that comes to need one, names its server here and in `BackCallTargets`.

- **The server's end is a loopback port per back-call** (`MCP__TUNNEL__BACKCALLS__<tag>=<port>`,
  `TunnelSettings.BackCalls`, served by `BackCallForwarders`; with no tunnel admitted a connection is
  closed at once). A client changes only its address — `ACCOUNTS__BASEURL`, `AGENT__BASEURL`,
  `STT__OPENAI__BASEURL`/`TTS__OPENAI__BASEURL`, `METRICS__URL` (`http://127.0.0.1:<port>/events`), the
  sandbox's `VFSBRIDGEURL` (`http://127.0.0.1:<port>/api/vfs-bridge`). A server names only the
  back-calls it makes: anything beside it, a sandbox command included, can dial those ports and reaches the allowlist and no more. Accepted
  connections are `NoDelay` (`ABackCallRequest_IsNotHeldBackByTheRelaysSocket`).
- **The relay's end is a Kestrel of its own** (`BackCallGateway` over `BackCallTransport`: one stream,
  one connection), so Kestrel reads the HTTP, never a parser of ours; the tunnel rides as the
  `BackCallOrigin` feature. Not the agent's Kestrel: a listener added in code replaces its configured
  ones. Another tag is reset; a non-POST, a path off its tag's list (a `..` in a bridge path included),
  or a server the tag is not for is 403, reported as `ErrorEvent` `BackCallRefused` — never the
  credential.
- **Every back-call it lets through says who asked**: `X-Ziggurat-Back-Call-Household` and
  `X-Ziggurat-Back-Call-Server` from the verified credential, after removing every header the server
  sent under that prefix. Bodies stream both ways; Kestrel's body limit is off because each target
  bounds its own.
- **Each target believes what it already believed.** The bridge trusts only its call token, passed through in `Authorization`; topic
  minting only the accounts assertion. **The accounts back-call is shown the tunnel's own credential**:
  the relay replaces `Authorization` with it and the accounts service checks that live credential itself
  (`InternalCallers.HouseholdServer` for `voice`, `HouseholdWebSearch` for `websearch`, channel-identity.md; any other server 403) rather than trusting
  the relay's allowlist alone — so tunnelled voice holds no deployment secret, no header is believed
  by a service every compose container can reach, and the household is the credential's. The relay's own `CALLERS__agent` is never lent out.
- **Metrics are published by the relay.** A server given `METRICS__URL` posts each event
  (`HttpMetricSink`) instead of publishing to Redis (so voice opens no Redis connection): one `MetricEvent` per POST,
  ≤ 64 KiB, else 400. The relay publishes it with `Origin` = the
  server's synthetic address and `TenantId` = the tunnel's household, over whatever the server said
  (`.claude/rules/observability.md`, "Households").
- **Tested at the round trip** (`BackCallGatewayTests`; `SandboxOverTunnelE2ETests` for the bridge).

## Outposts are tunnels too

An outpost (`.claude/rules/outpost.md`, which owns `TunnelledOutposts`) is a server whose credential
was minted for `outpost-<NAME>` (`Domain/Outposts/OutpostServerName.cs`): the prefix on the
**verified** name tells it apart, never anything on the open, and keeps an outpost from replacing an
own server of the same NAME. A consumer of `Open` or `WatchAsync` tells a machine apart with
`OutpostServerName.NameOf`: a channel follows its tunnel by its own server name, so never takes a machine for a channel; `TunnelListing`
deliberately shows a machine as `outpost-<NAME>`.
