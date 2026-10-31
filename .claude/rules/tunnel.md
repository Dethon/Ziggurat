---
paths:
  - "Mcp.Hosting/Tunnel/**"
  - "Mcp.Hosting/ToolServerExtensions.cs"
  - "Infrastructure/Tunnels/**"
  - "Infrastructure/Agents/IOwnServers.cs"
  - "Infrastructure/Agents/OwnServerEndpoints.cs"
  - "Infrastructure/Agents/McpServerEndpoint.cs"
  - "Infrastructure/Clients/Accounts/TunnelCredentialClient.cs"
  - "Agent/App/TunnelApi.cs"
  - "Agent/App/BrowsePagesApi.cs"
  - "Agent/App/RelayedCallsApi.cs"
  - "Infrastructure/Clients/Relay/**"
  - "Infrastructure/Clients/Voice/VoiceHubHttp.cs"
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
  a reason. The relay takes none yet (back-calls are ticket 08), so every server-opened stream is
  refused today.
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

## Exchanges the relay forwards to a household's server

Some requests arrive at the agent for a household's server rather than from a turn: a browse page a
person opens from a link, a shared server's call. **`TunnelForwarder`** (Infrastructure/Tunnels) forwards one such HTTP
exchange down the server's tunnel to the listener a tag names, and its answer back — a WebSocket
upgraded inside it included, whose bytes then run both ways until either end closes.

- It is **YARP's `IHttpForwarder`** (`Yarp.ReverseProxy`, `AddHttpForwarder()` in `AddTunnelRelay`)
  over `OpenTunnel.Forwarding(tag)`: an `HttpMessageInvoker` whose `ConnectCallback` opens a stream
  with that tag, one pool per tag per opening, set as a reverse proxy's must be — `UseCookies = false`
  above all, or the pool would keep the cookie one person's claim set and send it with the next
  person's request. A raw pipe of our own was the alternative; forwarding HTTP/1.1 well (hop-by-hop
  headers, streamed bodies, a client gone mid-response, a 101 handed to an opaque duplex stream) is
  what the library is, and the live view needs every part of it.
- The request goes on **as it came**: the base `HttpTransformer` (not `HttpTransformer.Default`,
  which adds X-Forwarded-* of its own and drops Host) copies Host and the proxy's `X-Forwarded-Proto`.
  The handoff page needs both — its live view's origin check compares `Origin` with the Host the
  browser asked for, and its cookie is `Secure` by `X-Forwarded-Proto`. Only the path is the caller's
  to choose, and the headers it withholds.
- The `mcp` tag's pool presents the tunnel credential and sends rotated keys first (`RelayHandler`),
  like `Client`; any other tag's presents nothing — a browser's request is not the agent's to vouch for.
- `ActivityTimeout` is 30 minutes, the longest a live view lasts: a view whose page stands still
  sends nothing and is not dead. The tunnel's end, or the browser's, still ends it at once.
- A forward that fails before anything was sent (the tunnel closed under it) is answered by the
  caller, as the server being offline.

### Browse pages

The proxy routes `/browse/*` on the main host to the agent (`BrowsePagesApi`, `BrowsePages.Path`),
never to the web search container (`DeploymentConformanceTests`: no route names it). `BrowsePages`
forwards the exchange **unchanged, path included,** to the household's web search under the
`pages` tag (`TunnelProtocol.PagesTag`), which web search registers for its pages listener
(`AddTunnelListener`); the stream lands on that listener's port, so `PagesListener`'s split holds as
before. The link, its claim, the cookie and the live view are the server's, unchanged. With no
tunnel open it answers **502 with a page of its own** saying the household's browser is offline.

### A shared server's calls to a household's server

A shared deployment server reaches a household's own server through the relay's internal endpoint,
`/internal/relay/<server name>/<the server's own path>` (`RelayedCallProtocol`, `RelayedCallsApi`,
`RelayedCalls`), **never the network** — and the proxy routes nothing under `/internal`
(`DeploymentConformanceTests`). Today one caller does: the timers server rings, dismisses and
validates satellites on the household's voice server (`.claude/rules/timers.md`).

- **The caller is checked against what the agent already holds**: it names itself
  (`X-Ziggurat-Caller`, its own `Mcp:Audience` — the name the agent dials it at) and presents the
  credential its own `/mcp` asks for, which is exactly the agent's `MCP__SECRETS__<name>` for it
  (`McpCredentials`). So no secret exists for this endpoint, the same rule the accounts service's
  callers follow, and one server's credential opens only what that server may reach. The client end
  is `RelayedCallCredential`, which a shared server adds when its base address is under the relay's
  path (`RelayedCallProtocol.IsRelayed`); a server dialled directly is presented neither.
- **What it may reach is a list in `RelayedCalls`**, by caller, household server and path prefix
  (`mcp-timers` → `voice` under `/api/voice`), checked on the path Kestrel normalised. A missing or
  wrong credential is 401, anything off the list 403; both are logged and published as an
  `ErrorEvent` (`RelayedCallRefused`), and neither forwards a byte.
- **Forwarded on the `mcp` tag**, whose pool presents the household server's tunnel credential
  (`RelayHandler`), with the caller's name and the deployment's `X-Announce-Token` withheld: nothing
  of the deployment's reaches a household. A tunnelled server's endpoints beside `/mcp` recognise the
  relay by `RelayCredential` (registered by `AddTunnel` while tunnelled): voice's `VoiceHubAuth` lets
  it through, so the kit's voice never holds the deployment's announce token.
- **Offline is a gateway's answer**: no tunnel open (or several), or a tunnel closing under the call,
  is **502 with the reason** in the body. `VoiceHubHttp` reads 502/504 as the hub being unreachable
  (`VoiceHubUnavailableException`), so a timer's create fails with `TransientDependency`, "the voice
  hub is unreachable, so the timer was not armed", and its fire is logged as not rung.

### Which household: `SoleHousehold`, a stopgap

Nothing in a browse link — nor in a timer's ring — names a household. In this phase only the home
household runs servers, so **a request that names none is for the one household whose server of
that name has its tunnel open** (`SoleHousehold.Find`, by `HouseholdServerNames`); none open, or
more than one, is answered as unreachable with the reason, never sent to whichever came first. Once
a second household runs servers (4b) the request must name its household: web search knows it where
the link is minted (`web_login`'s call token carries the person's account), and the timers server
knows it where a timer is created. The cookie's `Path=/browse` must then become per household too, or
one household's server would be sent another's view key.

## Not yet (later tickets)

- **04** connects a tunnelled channel server when its tunnel opens and drops it when it closes:
  consume `WatchAsync`.
- **05** reconciles a conversation's sessions at each turn's start and names offline servers. Today a
  household server that is offline, or a turn that names no household, **fails the session** as a
  configured endpoint does (ADR 0027): `OwnServerEndpoints` throws `HttpRequestException`. A call in
  flight when a tunnel drops fails with the stream's `IOException`, or the client's went-offline
  `HttpRequestException` on its next exchange.
- **11** must run web search under `HouseholdServerNames.WebSearch`: until it is tunnelled, the
  deployment's (and the smoke stack's) `/browse/*` answers the offline page, and
  `StackSmokeTests.TheBrowsePages_AreReachedThroughTheProxy…` with it. Likewise voice under
  `HouseholdServerNames.Voice`: the deployment's timers server already rings through the relay
  (`VOICEHUB__BASEURL`), so until voice is tunnelled a timer's create fails as the hub unreachable.
  The kit's voice keeps `Announce__Token` only for its own site's callers (Home Assistant's watch
  callback and resolve), never the deployment's value.
- **08** gives the relay an `accept` for server-opened streams, with its allowlist.
- **10** moves outposts onto the tunnel (their `Address` and verdicts are still URL strings).
- **11** switches the home's definitions to server names (and their whitelists to the bare names).
- **11/12**: the server's `ClientWebSocket` trusts the system's roots only, so a kit pointed at a
  smoke stack served by Caddy's local CA needs that CA trusted in the kit's images or a seam for it.
