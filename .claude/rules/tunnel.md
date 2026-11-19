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
over one WebSocket, and nothing more.**

- **Binary messages** are stream frames: type byte (`Open` 1, `Data` 2, `Close` 3, `Reset` 4),
  stream number as big-endian `uint32`, payload (tag / bytes / nothing / reason). Data frames ≤ 32 KiB;
  a message over 1 MiB closes the tunnel (`MessageTooBig`).
- **Text messages** are control messages, Domain's `TunnelControl` records (`TunnelProtocol.cs`, JSON
  with a `type` discriminator): `admitted` and `keys`, relay to server. Delivered **in order among the
  frames**, which is what lets a key arrive before the call signed with it.
- **Either end opens streams.** Relay numbers odd, server even. A stream the other end opened goes to
  that end's `accept`; with none, or a tag it does not take, it is reset with a reason. Server-opened
  streams are **back-calls** (below): handed to the back-call gateway once admitted, reset before.
- **A close from either end ends a stream in both directions.** HTTP/1.1 and WebSockets never
  half-close. A reset, or the tunnel's end, makes the next read throw `IOException` with the reason.
- **What arrives waits until it is read**, up to 64 frames per stream; past that the reader stops
  for the whole tunnel. A stream nobody reads holds up its own tunnel and no other.

Tested at the round trip (`TunnelRelayTests`), never frame by frame.

### A stream is a TCP connection to one of the server's own listeners

`TunnelClient` turns every relay-opened stream into a TCP connection to `127.0.0.1:<port>` of the
listener its tag names. So **no gate is rewritten and no request is handed to an in-process handler**:
`McpSecretGate`, `CallTokenGate` and `PagesListener` see a request exactly as if dialled, and an
upgraded WebSocket needs nothing more. The `mcp` tag (`TunnelProtocol.McpTag`) is the first bound
address unless registered otherwise; a second listener is named with
`services.AddTunnelListener(tag, port)`.

## Opening a tunnel

The server connects to `Mcp:Tunnel:Hub` (`wss://<main host>/tunnel`) with `Authorization: Bearer
<tunnel credential>` and `X-Ziggurat-Tunnel-Server: <name>` on the upgrade — **never in the URL**,
which the proxy logs. The relay (`Infrastructure/Tunnels/TunnelRelay.cs`) then:

- **Asks the accounts service on every open** (`TunnelCredentialClient`, `POST
  /accounts/internal/tunnels/verify`, as `CALLERS__agent` with `AGENTAPI__SHAREDSECRET`; contract in
  tunnel-credentials.md). A 404 is a refusal with its reason; any other failure is the service's, not the
  credential's. No credential at all is `Unknown` without asking.
- **Refused** (`Unknown`, `Revoked`, `AnotherServer`, `Suspended`): closed `PolicyViolation`, reason
  as close description, logged at warning, published as `ErrorEvent` (`Service` `agent`, `ErrorType`
  `TunnelRefused`, claimed name and reason — **never the credential**). A refusal of a known credential
  carries its household (`TunnelCredentialRefused.TenantId`) and the event is filed under and names it
  (so a revoked server knocking every minute says whose it is); `Unknown` is the deployment's.
- **Unverified** (accounts did not answer): closed `InternalServerError`, `Unverified`; not reported
  as a refusal; the server retries with backoff.
- **Admitted**: sent `TunnelAdmitted(TenantId, ServerName, Audience, Keys)` — household and name from
  the **verified credential**, never the claim; audience `ServerAddress.Tunnelled(household, name).Name`;
  the agent's published JWKS. Then registered.

How a tunnel ends (the close description the server reads):

- a refusal's reason;
- `Replaced` — a newer opening of the same server;
- `Revoked` — `TunnelRevocationListener` heard `accounts:tunnel-credentials:revoked` on the agent's
  Redis; the tunnel is removed from the registry first, so nothing reaches the server while the close
  is on its way;
- `Suspended` — the same listener heard `accounts:households:suspended` and closed every tunnel of
  that household, outposts included; next opens are refused `Suspended` until resumed, then servers
  return by themselves on the same credentials;
- `Restarting` — the agent is stopping: closed `EndpointUnavailable` so shutdown is not held open and
  servers come back with backoff.

## The server's end

`AddMcpHost` reads `McpGateSettings.Tunnel` (`TunnelSettings`). A server given all three of
`MCP__TUNNEL__HUB`, `MCP__TUNNEL__CREDENTIAL`, `MCP__TUNNEL__SERVERNAME` is tunnelled; otherwise it is
dialled. **No server writes tunnel code.** Tunnelled, the host:

- runs `TunnelClient` (`IHostedService`): waits for `ApplicationStarted`, opens, reopens after
  `ReconnectAfter` doubling to `ReconnectAtMost`, reset once admitted. A `PolicyViolation` close
  (refused, replaced, revoked) waits the most: a credential does not start working by itself, and two
  servers claiming one name must not flap.
- gates `/mcp` behind **the tunnel credential** instead of `Mcp:SharedSecret`; the relay presents it
  on every forwarded request (`RelayHandler`), so **no deployment holds a household server's secret** —
  the deployment's compose and `.env` gain nothing; the kit's `.env` holds one credential per server.
- checks call tokens against `TunnelTrust`: audience and keys handed over **this tunnel**, never
  `Mcp:Audience` or `Mcp:AgentKeysUrl`. Before admission no keys (`KeysUnavailable`); a `TunnelKeys`
  message replaces them.

**The hub's certificate** is checked as any HTTPS client does: system roots and the hub's name. A
deployment on its own CA (a smoke stack on Caddy's local CA) is trusted when
`MCP__TUNNEL__HUBCERTIFICATEAUTHORITY` names a PEM file — read once at start (not a certificate fails
the start); a chain the roots refuse for its chain alone is accepted if it chains to that root
(`X509ChainTrustMode.CustomRootTrust`). Caddy issues under an intermediate: the hub's intermediates
go into the build (`ExtraStore`), never trusted by themselves. A name mismatch or no certificate is
refused either way. Tested in `TunnelRelayTests` (root/intermediate/leaf like Caddy's) and on the
smoke stack.

Liveness is the WebSocket keep-alive on both ends (`KeepAlive`, `KeepAliveTimeout`). The four timings
are one shared policy file, `Domain/tunnel.json` (`mcp.tunnel`), read by `BindSettings` and by the
agent's `GetSettings` before the environment (`TunnelPolicyFileTests`).

## Keys

The relay sends the agent's published keys in `TunnelAdmitted`, then **before any exchange it
forwards, whenever `SigningKeyStore.PublishedKeys` differs from what it last sent** (`RelayHandler` →
`OpenTunnel.SendKeysIfChangedAsync`). The check is per exchange, not per pooled connection (the store
notices a rotation by its file's stamp), so keys go out ahead of the request's bytes and the server
holds them before reading the call. Retirement is delivered the same way. No public route serves the
agent's keys to a household.

## The registry: one opening is one generation

`TunnelRegistry` holds open tunnels, **at most one per (household, server name)**. Each `OpenTunnel`
is one connection generation (ADR 0012): `Generation` is process-wide and increasing, `Closed` is a
token cancelled when it goes, and `Client` is an `HttpClient` whose every connection is a stream to
the `mcp` listener, presenting the credential, sending rotated keys first, and refusing with
`HttpRequestException("<name> went offline: its tunnel closed")` once gone.

- `Find(tenantId, serverName)`: an open tunnel or none — one closing on its way out is none, so no
  caller checks `IsOpen` again. `Open`: a snapshot.
- `WatchAsync(ct)`: every open tunnel as `TunnelOpened`, then each `TunnelOpened` /
  `TunnelClosed(tunnel, reason)` as it happens, nothing missed between. A replacement publishes the
  old `TunnelClosed(Replaced)` before the new `TunnelOpened`.
- `StreamsTo(tenantId, serverName, tag)`: an `HttpMessageHandler` for the agent's **own** requests to
  a household server's listener (the household's Lemonade box via its forwarder's `chat` listener).
  Each connection is a stream of whatever `Find` answers **at that moment**, so a reopened generation
  is used at once and another household's same-named server never is; none open fails `"<name> is
  offline: its tunnel is not open"`. Presents nothing (not `/mcp`). Its pool is the current tunnel's
  alone (`CurrentTunnelStreams`): every request asks `Find` first and drops a stale pool, because a
  revoked tunnel leaves the registry before its close reaches the server and its streams still carry
  bytes in between. Tested in `TunnelStreamsTests`.
- A server that reconnects before its old socket times out **replaces** it (old closed `Replaced`).
  Nothing carries over between generations.

## Back-calls

What a household's server still needs of the deployment it asks for over its own tunnel, and only
that: a stream it opens with one of `BackCallProtocol`'s tags, carrying HTTP/1.1.

| Tag | From (server name) | Reaches | Paths (all POST) | For |
|---|---|---|---|---|
| `accounts` | `voice` only | the accounts service (`Accounts:BaseUrl`) | `/accounts/internal/resolve`, `/accounts/internal/guest` | voice's speakers and guest |
| `topics` | `voice` only | the agent's own `TopicMinting.Path` | `/internal/topics` | voice's topics |
| `lemonade` | `voice` only | the shared Lemonade (`backCalls:lemonadeUrl`) | `/v1/audio/transcriptions`, `/v1/audio/speech` | voice's STT and TTS |
| `vfs-bridge` | `sandbox` only | the agent's own exec bridge | under `/api/vfs-bridge/` | the sandbox's FUSE daemon |
| `metrics` | any but an outpost | the relay itself | `BackCallProtocol.MetricsPath` (`/events`) | any server's metric events; the sandbox's own heartbeat alone (`BackCallTargets.MayPublish`: its container runs the model's commands, which can dial the port) |

**A back-call is for the servers that make it**, by the verified credential's server name
(`HouseholdServerNames.Voice`/`.Sandbox`; an outpost is `outpost-<NAME>` and publishes no metrics).
The accounts back-call mints a voice assertion for anyone in the household, and topics are minted on
such assertions, so a printer, vault, library or outpost must not reach them; nor voice the bridge. A
new back-call, or a server that comes to need one, names its server here and in `BackCallTargets`.

- **The server's end is a loopback port per back-call.** `MCP__TUNNEL__BACKCALLS__<tag>=<port>`
  (`TunnelSettings.BackCalls`): `BackCallForwarders` listens on `127.0.0.1:<port>` and turns each
  connection into a stream under that tag (`TunnelLink`, set by `TunnelClient` on admission, cleared at
  the tunnel's end; with none, the connection is closed at once). A client changes only its address —
  `ACCOUNTS__BASEURL`, `AGENT__BASEURL`, `STT__OPENAI__BASEURL`/`TTS__OPENAI__BASEURL`, `METRICS__URL`
  (`http://127.0.0.1:<port>/events`), the sandbox's `VFSBRIDGEURL`
  (`http://127.0.0.1:<port>/api/vfs-bridge`) — and the Rust daemon is served as a .NET client is. A
  server names only the back-calls it makes: anything beside it, a sandbox command included, can dial
  those ports and reaches the allowlist and no more. Accepted connections are `NoDelay` (with Nagle the
  body waited ~45 ms per back-call; `ABackCallRequest_IsNotHeldBackByTheRelaysSocket`); the
  `TunnelClient` end was measured and is not held back, so it is left as it is.
- **The relay's end is a Kestrel of its own** (`BackCallGateway`, started with the relay), whose
  transport is the back-call streams (`BackCallTransport`: one stream, one connection), so Kestrel
  reads the HTTP — keep-alive and chunked included — never a parser of ours; the tunnel rides on each
  connection as the `BackCallOrigin` feature. Not the agent's Kestrel: a listener added in code
  replaces its configured ones. Another tag is reset; a non-POST, a path off its tag's list (a `..`
  in a bridge path included), or a server the tag is not for is 403; both logged and reported
  (`ErrorEvent`, `ErrorType` `BackCallRefused`, naming server and household, never the credential).
- **Every back-call it lets through says who asked**: `X-Ziggurat-Back-Call-Household` and
  `X-Ziggurat-Back-Call-Server` from the verified credential, after removing every header the server
  sent under that prefix. Hop-by-hop headers and `Host` are the forward's own; bodies stream both ways
  (a spoken reply is read while Lemonade makes it); Kestrel's body limit is off because each target
  bounds its own. The agent's own routes are asked at `127.0.0.1` on the port of its first `IServer`
  address, so need no setting.
- **Each target believes what it already believed.** The bridge trusts only its call token (passed in
  `Authorization`); topic minting only the accounts assertion. **The accounts back-call is shown the
  tunnel's own credential**: the relay replaces `Authorization` with it and the accounts service checks
  that live credential itself (`InternalCallers.HouseholdServer`, channel-identity.md: `voice` only, else 403)
  rather than trusting the relay's allowlist as the only gate — so tunnelled voice holds no deployment
  secret (its resolver's empty one is replaced), no header is believed by a service every compose
  container can reach, and the household is the credential's. The relay's own `CALLERS__agent` is not
  lent out: SexyTime's server holds that too.
- **Metrics are published by the relay.** A server given `METRICS__URL` posts each event
  (`HttpMetricSink`) instead of publishing to Redis (`AddMetricsPublishing(name, feed)`), so voice
  opens no Redis connection; the relay reads it (≤ 64 KiB, a `MetricEvent` or 400) and publishes with
  `Origin` = the server's synthetic address and `TenantId` = the tunnel's household, over whatever the
  server said (dashboard keying: `.claude/rules/observability.md`, "Households").
- **Tested at the round trip** (`BackCallGatewayTests`: each back-call through a real port and relay;
  `SandboxOverTunnelE2ETests`: the sandbox image's bridge back-call).

## Outposts are tunnels too

An outpost (`.claude/rules/outpost.md`, which owns `TunnelledOutposts`) is a server whose credential
was minted for `outpost-<NAME>` (`Domain/Outposts/OutpostServerName.cs`): the prefix on the
**verified** name tells it apart, never anything on the open, and it keeps an outpost from replacing
an own server of the same NAME in the registry. A consumer of `Open` or `WatchAsync` tells a machine
apart with `OutpostServerName.NameOf`: a channel follows its tunnel by its own server name, so never
takes a machine for a channel, and `TunnelListing` deliberately shows a machine as `outpost-<NAME>`
beside the household's servers.
