---
paths:
  - "Mcp.Hosting/Tunnel/**"
  - "Mcp.Hosting/ToolServerExtensions.cs"
  - "Infrastructure/Tunnels/**"
  - "Infrastructure/Agents/IOwnServers.cs"
  - "Infrastructure/Agents/IOwnChannelServers.cs"
  - "Agent/App/ChannelConnectionHost.cs"
  - "Infrastructure/Agents/OwnServerEndpoints.cs"
  - "Infrastructure/Agents/McpServerEndpoint.cs"
  - "Infrastructure/Clients/Accounts/TunnelCredentialClient.cs"
  - "Infrastructure/Metrics/HttpMetricSink.cs"
  - "Agent/App/TunnelApi.cs"
  - "Agent/Modules/TunnelModule.cs"
  - "Domain/DTOs/Tunnel/**"
  - "Domain/tunnel.json"
  - "DockerCompose/**"
  - "Tests/Integration/Tunnels/**"
  - "Tests/InProcessOwnServers.cs"
---

# The tunnel

A household's **own server** (CONTEXT.md, "Households") is never dialled. It opens one outbound
WebSocket — its **tunnel** — to the **relay** in the agent, through the proxy's one route for it, and
everything the agent says to it travels inside. The spec is `.scratch/tenants-tunnel/spec.md`; this is
what the code holds to.

## The wire

`Mcp.Hosting/Tunnel/TunnelMux.cs` is the whole protocol, used at both ends: **a stream multiplexer
over one WebSocket, and nothing more.**

- **Binary messages** are stream frames: one byte (`Open` 1, `Data` 2, `Close` 3, `Reset` 4), the
  stream's number as a big-endian `uint32`, then the payload — the tag for an open, bytes for data,
  nothing for a close, the reason for a reset. A data frame carries at most 32 KiB; a message over
  1 MiB closes the tunnel (`MessageTooBig`).
- **Text messages** are control messages, Domain's `TunnelControl` records (`TunnelProtocol.cs`,
  JSON with a `type` discriminator): `admitted` and `keys`, relay to server. They are handed to their
  end **in order among the frames**, which is what lets a key arrive before the call signed with it.
- **Either end opens streams.** The relay numbers its own odd, the server even. A stream the other
  end opened goes to that end's `accept`; with none, or with a tag it does not take, it is reset with
  a reason. The server-opened ones are its **back-calls** (below): the relay's accept hands them to
  the back-call gateway once the tunnel is admitted, and resets one opened before.
- **A close from either end ends a stream in both directions.** HTTP/1.1 and a WebSocket upgraded
  inside it never half-close. A reset, or the tunnel's own end, makes the next read throw
  `IOException` with the reason.
- **What arrives waits until it is read**, up to 64 frames per stream; past that the reader stops
  for the whole tunnel. A stream nobody reads holds up its own tunnel and no other.

Tested at the round trip (`TunnelRelayTests`), never frame by frame.

### A stream is a TCP connection to one of the server's own listeners

`TunnelClient` turns every stream the relay opens into a TCP connection to `127.0.0.1:<port>` of the
listener its tag names, and pipes bytes both ways. So **no gate is rewritten and no request is handed
to an in-process handler**: `McpSecretGate`, `CallTokenGate` and `PagesListener` see a request exactly
as if it had been dialled, and a WebSocket upgraded inside a stream needs nothing more (ticket 06).
The `mcp` tag (`TunnelProtocol.McpTag`) is the first address the server bound unless it registers
another; a server with a second listener names it with `services.AddTunnelListener(tag, port)`.

## Opening a tunnel

The server connects to `Mcp:Tunnel:Hub` (`wss://<main host>/tunnel`) with `Authorization: Bearer
<tunnel credential>` and `X-Ziggurat-Tunnel-Server: <name>` on the upgrade — **never in the URL**,
which the proxy logs. The relay (`Infrastructure/Tunnels/TunnelRelay.cs`) accepts the WebSocket and
then:

- **Asks the accounts service on every open** (`TunnelCredentialClient`, `POST
  /accounts/internal/tunnels/verify`, as `CALLERS__agent` with `AGENTAPI__SHAREDSECRET`). A 404 is a
  refusal with its reason; any other failure is the service's, not the credential's.
- **Refused** (`Unknown`, `Revoked`, `AnotherServer`): closed `PolicyViolation` with the reason as the
  close description, logged at warning, and published as an `ErrorEvent` (`Service` `agent`,
  `ErrorType` `TunnelRefused`, the claimed name and the reason — **never the credential**). No
  credential at all is `Unknown` without asking.
- **Unverified** (the accounts service did not answer): closed `InternalServerError`, `Unverified`.
  Not reported as a refusal; the server retries with backoff.
- **Admitted**: the server is sent `TunnelAdmitted(TenantId, ServerName, Audience, Keys)` — the
  household and name from the **verified credential**, never from what the server claimed; the
  audience its call tokens are signed for (`ServerAddress.Tunnelled(household, name).Name`); the
  agent's published JWKS. Then it is registered.

How a tunnel ends, as the close description the server reads: a refusal's reason; `Replaced` (a newer
opening of the same server); `Revoked` (`TunnelRevocations` heard the accounts service's
`accounts:tunnel-credentials:revoked` on the agent's Redis and closed the tunnel that credential
opened — it is removed from the registry first, so nothing reaches the server while the close is on
its way); `Restarting` (the agent is stopping: every tunnel is closed `EndpointUnavailable` so the
agent's shutdown is not held open and servers come back with backoff).

## The server's end

`AddMcpHost` reads `McpGateSettings.Tunnel` (`TunnelSettings`, Domain/DTOs/Tunnel). A server given a
hub, a credential and its name (`MCP__TUNNEL__HUB`, `MCP__TUNNEL__CREDENTIAL`,
`MCP__TUNNEL__SERVERNAME`) is tunnelled; without all three it is dialled exactly as before. **No server
writes tunnel code.** Tunnelled, the host:

- runs `TunnelClient` (a plain `IHostedService`), which waits for `ApplicationStarted`, opens the
  tunnel, and reopens it after `ReconnectAfter`, doubling to `ReconnectAtMost`; back to the first once
  admitted. A `PolicyViolation` close (refused, replaced, revoked) waits the most: a credential does
  not start working by itself, and two servers claiming one name must not flap.
- gates `/mcp` behind **the tunnel credential** instead of `Mcp:SharedSecret`. The relay presents the
  credential the tunnel was opened with on every request it forwards (`RelayHandler`), so **no
  deployment holds a household server's secret** — the deployment's compose and `.env` gain nothing,
  and the tenant kit's `.env` holds one credential per server.
- checks call tokens against `TunnelTrust`: the audience and the keys the relay handed over **this
  tunnel**, never `Mcp:Audience` or `Mcp:AgentKeysUrl`. Before admission there are no keys
  (`KeysUnavailable`); a `TunnelKeys` message replaces them.

Liveness is the WebSocket's own keep-alive on both ends (`KeepAlive`, `KeepAliveTimeout`). The four
timings are one shared policy file, `Domain/tunnel.json` (`mcp.tunnel`), read by `BindSettings` and
by the agent's `GetSettings` before the environment (`TunnelPolicyFileTests`).

## Keys

The relay sends the agent's published keys in `TunnelAdmitted`, then **before any exchange it
forwards, whenever `SigningKeyStore.PublishedKeys` differs from what it last sent** (`RelayHandler` →
`OpenTunnel.SendKeysIfChangedAsync`). The store notices a rotation by its file's stamp when read, so
the check is per exchange, not per pooled connection: the keys go out on the same WebSocket ahead of
the request's bytes and the server holds them before it reads the call. A retirement is delivered the
same way. No public route serves the agent's keys to a household.

## The registry: one opening is one generation

`TunnelRegistry` (Infrastructure/Tunnels) holds the open tunnels, **at most one per (household,
server name)**. Each `OpenTunnel` is one connection generation in ADR 0012's sense:

- `TenantId`, `ServerName`, `CredentialId`, `Address` (the synthetic `ServerAddress`), `Generation`
  (process-wide, increasing), `OpenedAt`, `IsOpen`, `Closed` (a token cancelled when it goes), and
  `Client` — an `HttpClient` whose every connection is a stream of this tunnel to the server's `mcp`
  listener, which presents the credential, sends rotated keys first, and refuses with
  `HttpRequestException("<name> went offline: its tunnel closed")` once the tunnel is gone.
- `Find(tenantId, serverName)`, `Open` (a snapshot), and `WatchAsync(ct)`: every open tunnel as a
  `TunnelOpened`, then every `TunnelOpened` / `TunnelClosed(tunnel, reason)` as it happens, with
  nothing missed between the two. A replacement publishes the old one's `TunnelClosed(Replaced)`
  before the new one's `TunnelOpened`.
- A server that reconnects while its old socket has not timed out **replaces** it: the old opening is
  closed `Replaced`. Nothing carries over between generations.

## Back-calls

What a household's server still needs of the deployment it asks for over its own tunnel, and only
that: a stream it opens with one of `BackCallProtocol`'s tags, carrying HTTP/1.1.

| Tag | Reaches | Paths (all POST) | For |
|---|---|---|---|
| `accounts` | the accounts service (`Accounts:BaseUrl`) | `/accounts/internal/resolve`, `/accounts/internal/guest` | voice's speakers and guest |
| `topics` | the agent's own `TopicMinting.Path` | `/internal/topics` | voice's topics |
| `lemonade` | the shared Lemonade (`backCalls:lemonadeUrl`) | `/v1/audio/transcriptions`, `/v1/audio/speech` | voice's STT and TTS |
| `vfs-bridge` | the agent's own exec bridge | under `/api/vfs-bridge/` | the sandbox's FUSE daemon |
| `metrics` | the relay itself | `BackCallProtocol.MetricsPath` (`/events`) | any server's metric events |

- **The server's end is a loopback port per back-call.** `MCP__TUNNEL__BACKCALLS__<tag>=<port>`
  (`TunnelSettings.BackCalls`): `BackCallForwarders` listens on `127.0.0.1:<port>` and turns every
  connection into a stream of the admitted tunnel under that tag (`TunnelLink`, which `TunnelClient`
  sets on admission and clears when the tunnel ends; with none, the connection is closed at once). So
  a client changes nothing but its address — `ACCOUNTS__BASEURL`, `AGENT__BASEURL`,
  `STT__OPENAI__BASEURL`/`TTS__OPENAI__BASEURL`, `METRICS__URL` (`http://127.0.0.1:<port>/events`),
  the sandbox's `VFSBRIDGEURL` (`http://127.0.0.1:<port>/api/vfs-bridge`) — and the Rust daemon is
  served as a .NET client is. A server names only the back-calls it makes: anything beside it, a
  sandbox command included, can dial those ports, and what it reaches is then the allowlist and no more.
- **The relay's end is a Kestrel of its own** (`BackCallGateway`, started with the relay). Its
  transport is the back-call streams themselves (`BackCallTransport`: one stream, one connection),
  so the HTTP inside is read by Kestrel, keep-alive and chunked bodies included, and never by a parser
  of ours; the tunnel rides on every connection as the `BackCallOrigin` feature. It is not the agent's
  Kestrel because a listener added in code replaces the agent's configured ones rather than joining
  them. A stream with another tag is reset; a request that is not a POST to its tag's paths (a `..`
  in a bridge path included) is answered 403; both are logged and reported (`ErrorEvent`, `Service`
  `agent`, `ErrorType` `BackCallRefused`, naming the server and its household, never the credential).
- **Every back-call it lets through says who asked**: `X-Ziggurat-Back-Call-Household` and
  `X-Ziggurat-Back-Call-Server`, from the tunnel's verified credential, after removing every header
  the server sent under that prefix. Hop-by-hop headers and `Host` are the forward's own; the body
  streams both ways (a spoken reply is read while Lemonade makes it), and Kestrel's body limit is off
  because each target bounds its own. The agent's own routes are asked where the agent listens (the
  port of its first `IServer` address, on `127.0.0.1`), so they need no setting.
- **Each target believes what it already believed.** The bridge still trusts only its call token, which
  passes through in `Authorization`; topic minting still trusts only the accounts service's assertion.
  **The accounts back-call is shown the tunnel's own credential**: the relay replaces `Authorization`
  with the credential the tunnel was opened with, and the accounts service reads who asks off that
  live credential itself (`InternalCallers.HouseholdServer`, accounts.md) — so a tunnelled voice holds
  no deployment secret (its resolver presents an empty one, which is replaced), no header is believed
  by a service every compose container can reach, and the household it may ask about is the
  credential's. The relay's own `CALLERS__agent` is not lent out: SexyTime's server holds that too.
- **Metrics are published by the relay.** A server given `METRICS__URL` posts each event
  (`HttpMetricSink`, the feed's JSON) instead of publishing onto Redis (`AddMetricsPublishing(name,
  feed)`), so voice opens no Redis connection at all; the relay reads the event (64 KiB at most, a
  `MetricEvent` or 400) and publishes it through its own publisher with `Origin` set to the server's
  synthetic address. Two households' servers of one service still share a heartbeat's `Service` on
  the dashboard's roster; telling them apart there is 4b's.
- **Tested at the round trip** (`BackCallGatewayTests`: each back-call through a real server's port and
  a real relay, reading what the target saw; `SandboxOverTunnelE2ETests`: the sandbox image tunnelled,
  a command's `cat` and `>` on another mount through its bridge back-call).

## Reaching a household's server from a turn

- **An agent definition names a household's server by its name** — a bare DNS label
  (`TunnelCredentialProtocol.IsServerName`), beside the URLs of shared servers: `["vault",
  "http://mcp-scheduling:8080/mcp"]`. `AddAgent` refuses an endpoint that is neither an http(s) URL nor
  a server's name, and `ServerAddress.FromUrl` refuses anything that is not http(s) with a host, so the
  two can never be confused.
- The projection makes it `McpServerEndpoint.OwnServer(name)` (`NamesOwnServer`, no credential). The
  agent's household travels as data: `ConversationGroup` passes the verified identity's tenant — a
  person's or **the guest's** — to `IAgentFactory.Create(..., tenantId)`; it lands on
  `AgentSpec.TenantId`, and a worker takes its parent's through `SpawnContext.TenantId`.
- **Resolved when a session is built** (`OwnServerEndpoints`, beside the outposts in
  `McpAgent.GetOrCreateSessionAsync`), in the definition's order, through **`IOwnServers`** — the
  resolver seam. The deployment's is `RelayedOwnServers`: the open tunnel's
  `McpServerEndpoint.Relayed(address, client)`, bound to that generation. A stack that hosts its
  servers in process registers its own: the eval (`EvalStack.OwnServers`: a name stands for the
  compose server `mcp-<name>`) and integration tests (`Tests/InProcessOwnServers.cs`), which answer
  the hosted server's URL — **no tunnel runs in either**.
- A registered agent may name a household server too: the name cannot point anywhere but the turn's
  own household's server, and the call token still names the person.

### The synthetic address

`ServerAddress.Tunnelled(household, name)`: `Name` = `<name>.<household>.tunnel` — the call token's
audience, a hostname the relay's client puts in requests without it resolving anywhere; `Qualifier` =
the server's name alone, so its tools are `mcp__vault__…` for every household and a whitelist reads the
same everywhere; `CacheKey` = the full name, so the prompt cache (one per process) never serves one
household's server prompts to another. A URL's `CacheKey` is its qualifier, as before.

## A household's channel server

A server that is also a channel (web search, library, Home Assistant, voice) is named in
`channelEndpoints` **by its server name instead of a URL** — `{ "channelId": "homeassistant",
"endpoint": "homeassistant" }` — and `AddAgent` refuses a channel endpoint that is neither. Such an
endpoint is **the home household's server of that name**: a channel connection is one per channel id,
for the deployment, and in this phase the operator's household is the only one with servers. The
agent learns which household is the home from the accounts service, not from a setting: the verify
answer carries `TunnelCredentialVerified.Home` (the credential's household `IsHome`), and the opening
keeps it as `OpenTunnel.Home`.

- `ChannelConnectionHost` runs a URL endpoint once, as before, presenting `McpCredentials.For(url)`
  (the host hands the credential with the endpoint now: `IMcpChannelConnection.RunAsync` takes a
  `McpServerEndpoint`). A server-name endpoint it **follows**: `IOwnChannelServers.WatchAsync(name)`
  (the deployment's is `RelayedOwnServers`, over `TunnelRegistry.WatchAsync`) yields every opening of
  the home's tunnel of that name as an `OwnServerOpening` — the `Relayed` endpoint and the opening's
  `Closed` token — and the host runs the connection once per opening, on a token linked to it. A newer
  opening (a replacement, a reconnect) finishes the old run before the new one starts, since one
  connection holds one client.
- **A run that returns lets go**: `McpChannelConnection.RunAsync` stops its pump and disposes its
  client whenever its token goes, so a channel whose tunnel closed is **not connected** (ADR 0011's
  five behaviours) until the next opening, and the next opening starts from nothing — its tool set is
  asked again (ADR 0012). The run's registration, health checks and `channel_receive` long-poll are
  the dialled channel's, carried over the tunnel's client and signed for the synthetic address.
- Nothing about a turn changes: a fire through the tunnel carries the delegation the server stored and
  is checked as every fire is (`.claude/rules/service-identity.md`, "Deferred turns"); a reply goes
  back through whichever opening is current.
- A stack hosting its servers in process names its channels by URL; nothing there follows a tunnel.

Tested at the round trip (`TunnelledChannelTests`: a notification and its reply, a drop and a reopen
without a restart, a watch's delegated fire through the real Home Assistant server) and in
`ChannelConnectionHostTests` (one run per opening, never two at once).

## Not yet (later tickets)

- **05** reconciles a conversation's sessions at each turn's start and names offline servers. Today a
  household server that is offline, or a turn that names no household, **fails the session** as a
  configured endpoint does (ADR 0027): `OwnServerEndpoints` throws `HttpRequestException`. A call in
  flight when a tunnel drops fails with the stream's `IOException`, or the client's went-offline
  `HttpRequestException` on its next exchange.
- **06** adds the `pages` tag (`AddTunnelListener`) and routes `/browse/*` to the relay.
- **09** lets shared servers reach a household's server through the relay.
- **10** moves outposts onto the tunnel (their `Address` and verdicts are still URL strings).
- **11** switches the home's definitions to server names (and their whitelists to the bare names).
- **11/12**: the server's `ClientWebSocket` trusts the system's roots only, so a kit pointed at a
  smoke stack served by Caddy's local CA needs that CA trusted in the kit's images or a seam for it.
