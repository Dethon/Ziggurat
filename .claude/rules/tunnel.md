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
  - "Infrastructure/Agents/McpAgent.cs"
  - "Infrastructure/Agents/Mcp/QualifiedMcpTool.cs"
  - "Agent/Commands/AgentCommand.cs"
  - "Infrastructure/Clients/Accounts/TunnelCredentialClient.cs"
  - "Infrastructure/Metrics/HttpMetricSink.cs"
  - "Agent/App/TunnelApi.cs"
  - "Agent/App/BrowsePagesApi.cs"
  - "Agent/App/RelayedCallsApi.cs"
  - "Infrastructure/Clients/Relay/**"
  - "Infrastructure/Clients/Voice/VoiceHubHttp.cs"
  - "Agent/Modules/TunnelModule.cs"
  - "Domain/DTOs/Tunnel/**"
  - "Domain/tunnel.json"
  - "DockerCompose/**"
  - "TenantKit/**"
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
- **Refused** (`Unknown`, `Revoked`, `AnotherServer`, `Suspended`): closed `PolicyViolation` with the reason as the
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
opening of the same server); `Revoked` (`TunnelRevocationListener` heard the accounts service's
`accounts:tunnel-credentials:revoked` on the agent's Redis and closed the tunnel that credential
opened — it is removed from the registry first, so nothing reaches the server while the close is on
its way); `Suspended` (the same listener heard `accounts:households:suspended` and closed every open
tunnel of that household, outposts included, the same way — its servers' next opens are refused
`Suspended` until the operator resumes it, and then come back by themselves on the same credentials);
`Restarting` (the agent is stopping: every tunnel is closed `EndpointUnavailable` so the
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

**The hub's certificate** is checked as any HTTPS client checks one: the system's roots, and the
hub's name. A deployment served by a certificate authority of its own — a smoke stack on Caddy's local
CA — is trusted by a server told that CA: `MCP__TUNNEL__HUBCERTIFICATEAUTHORITY` names a PEM file, read
once at start (a file that is not a certificate fails the start), and a certificate the roots refuse
for its chain alone is accepted if it chains to that one (`X509ChainTrustMode.CustomRootTrust`). The
CA named is a root, and Caddy issues under an intermediate it serves beside the certificate: the
intermediates the hub sent are offered to the chain's build (`ExtraStore`), never trusted by
themselves. A name that does not match, or no certificate, is refused either way. Tested at the round
trip (`TunnelRelayTests`, on a test authority shaped like Caddy's — root, intermediate, leaf: a relay
on its certificate is never joined, joined once told, and never joined under another name) and on the
smoke stack, whose kit servers trust Caddy's local root this way.

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
- `Find(tenantId, serverName)` (an open tunnel or none — one closing on its way out is none, so no caller checks `IsOpen` again), `Open` (a snapshot), and `WatchAsync(ct)`: every open tunnel as a
  `TunnelOpened`, then every `TunnelOpened` / `TunnelClosed(tunnel, reason)` as it happens, with
  nothing missed between the two. A replacement publishes the old one's `TunnelClosed(Replaced)`
  before the new one's `TunnelOpened`.
- `StreamsTo(tenantId, serverName, tag)`: an `HttpMessageHandler` for the agent's **own** requests to
  a listener of one household's server (the household's own Lemonade box, through its forwarder's
  `chat` listener). Every connection is a stream of the tunnel `Find` answers **at that moment**, so a
  reopened generation is used at once and another household's server of the same name never is; with
  none open the connection fails `"<name> is offline: its tunnel is not open"`. It presents nothing —
  the listener is not the server's `/mcp` — and a pooled connection of a closed generation ends with it.
  Tested at the round trip (`TunnelStreamsTests`).
- A server that reconnects while its old socket has not timed out **replaces** it: the old opening is
  closed `Replaced`. Nothing carries over between generations.

## Back-calls

What a household's server still needs of the deployment it asks for over its own tunnel, and only
that: a stream it opens with one of `BackCallProtocol`'s tags, carrying HTTP/1.1.

| Tag | From (server name) | Reaches | Paths (all POST) | For |
|---|---|---|---|---|
| `accounts` | `voice` only | the accounts service (`Accounts:BaseUrl`) | `/accounts/internal/resolve`, `/accounts/internal/guest` | voice's speakers and guest |
| `topics` | `voice` only | the agent's own `TopicMinting.Path` | `/internal/topics` | voice's topics |
| `lemonade` | `voice` only | the shared Lemonade (`backCalls:lemonadeUrl`) | `/v1/audio/transcriptions`, `/v1/audio/speech` | voice's STT and TTS |
| `vfs-bridge` | `sandbox` only | the agent's own exec bridge | under `/api/vfs-bridge/` | the sandbox's FUSE daemon |
| `metrics` | any but an outpost | the relay itself | `BackCallProtocol.MetricsPath` (`/events`) | any server's metric events |

**A back-call is for the servers that make it**, by the server name the tunnel's credential was
minted for (`HouseholdServerNames.Voice`/`.Sandbox`; an outpost is `outpost-<NAME>`, which publishes
no metrics). The accounts back-call mints a signed voice assertion for any person of the household by
user id, and topics are minted on such assertions, so a household's printer, vault or library, or an
outpost on a member's laptop, holding only its own credential must not reach them; nor voice the
bridge. A new back-call, or a server that comes to need one, names its server here and in
`BackCallTargets`.

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
  in a bridge path included), or that comes from a server its tag is not for, is answered 403; both
  are logged and reported (`ErrorEvent`, `Service`
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
  live credential itself (`InternalCallers.HouseholdServer`, accounts.md), admitting a credential
  minted for `voice` alone and answering any other live one 403 rather than trusting the relay's
  allowlist to be the only gate — so a tunnelled voice holds
  no deployment secret (its resolver presents an empty one, which is replaced), no header is believed
  by a service every compose container can reach, and the household it may ask about is the
  credential's. The relay's own `CALLERS__agent` is not lent out: SexyTime's server holds that too.
- **Metrics are published by the relay.** A server given `METRICS__URL` posts each event
  (`HttpMetricSink`, the feed's JSON) instead of publishing onto Redis (`AddMetricsPublishing(name,
  feed)`), so voice opens no Redis connection at all; the relay reads the event (64 KiB at most, a
  `MetricEvent` or 400) and publishes it through its own publisher with `Origin` set to the server's
  synthetic address and `TenantId` to the tunnel's household, both over whatever the server said.
  The dashboard keys roll-ups and heartbeats by that household (`.claude/rules/observability.md`,
  "Households"), so two households' servers of one service are two tiles.
- **Tested at the round trip** (`BackCallGatewayTests`: each back-call through a real server's port and
  a real relay, reading what the target saw; `SandboxOverTunnelE2ETests`: the sandbox image tunnelled,
  a command's `cat` and `>` on another mount through its bridge back-call).

## Reaching a household's server from a turn

- **An agent definition names a household's server by its name** — a bare DNS label
  (`TunnelCredentialProtocol.IsServerName`), beside the URLs of shared servers: `["vault",
  "http://mcp-scheduling:8080/mcp"]`. `AddAgent` refuses an endpoint that is neither an http(s) URL nor
  a server's name, and `ServerAddress.FromUrl` refuses anything that is not http(s) with a host, so the
  two can never be confused.
- The projection makes it `McpServerEndpoint.OwnServer(name)` (`NamesOwnServer`, no credential). Every reader of a configured endpoint — the projection, the channel host, the key commands — reads it through `McpServerEndpoint.Of(address, credential)`, and every dial builds its client through `McpClientManager.TransportFor` (`McpEndpointTransport` outside Infrastructure): nothing else tests `IsServerName` on an endpoint or presents a credential by hand. The
  agent's household travels as data: `ConversationGroup` passes the verified identity's tenant — a
  person's or **the guest's** — to `IAgentFactory.Create(..., tenantId)`; it lands on
  `AgentSpec.TenantId`, and a worker takes its parent's through `SpawnContext.TenantId`.
- **Resolved at every turn's start** (`OwnServerEndpoints`, beside the outposts in
  `McpAgent.GetOrCreateSessionAsync`), in the definition's order, through **`IOwnServers`** — the
  resolver seam. The deployment's is `RelayedOwnServers`: the open tunnel's
  `OpenTunnel.Endpoint` (`McpServerEndpoint.Relayed`: its address, client, generation and `Closed`), bound to that generation. A stack that hosts its
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

## Offline servers

**An exception to ADR 0027, for tunnel endpoints only** (the ADR itself is not edited; this is where
the exception is recorded). A household's server named in a definition is `Configured` — the
deployment's settings named it — yet one that is not connected **does not fail the session**: it is
left out of the turn and named in its context. A machine at home being off is not the deployment being
broken. Shared servers' URL endpoints keep ADR 0027's rule: retried, then fatal.

- **Reconciled at each turn's start.** `OwnServerEndpoints.Resolve` answers an `OwnServerResolution`:
  the endpoints to dial with every unreachable household server left out, the names left out, and one
  `ServerState(name, online, generation, withheld)` per named server. A server the turn withholds by name
  — one its household never set up, or one a worker's parent does not name
  (`.claude/rules/household-config.md`, "The agents a household runs") — is neither dialled nor among the
  names left out, so it is never on the offline line; its state still says so, and a turn after the
  household sets it up builds a new session that reaches it. `McpAgent` keeps it beside the session
  and compares it with the next turn's (`ReachesTheSameServersAs`): equal, the session is reused;
  different — a server came online, went offline, or **reconnected as a new tunnel generation** — the
  old session is disposed and a new one built, outposts and shared servers redialled with it. An
  agent that uses outposts (its own `usesOutposts`, a worker's ANDed with its parent's, ADR 0028)
  compares its machines the same way (`ComposedEndpoints.ReachesTheSameMachinesAs`: each live
  outpost's name and tunnel generation, as the same `ServerState`s), so a machine that opens, closes or reopens joins or leaves
  at the next turn too; an agent that does not never asks. That is
  how a new generation's tools enter (ADR 0012): only at a turn's start, never under a running turn.
  Turns of one conversation group run one at a time, so the session replaced has no call in flight.
  A stack hosting servers in process has no generations (`InProcessOwnServers` answers null), so there
  presence alone is the key.
- **Unreachable** is no tunnel of that name open for the household, or a turn that names no household
  (the relay answers null for both). A tunnel that closes between the lookup and the dial is dialled
  once, never retried, and dropped like an offline one (`McpClientManager`, on `McpServerEndpoint.Offline`
  being cancelled); failing any other way it still fails the session.
- **The context line** is stamped on the turn's own user message as data (`SetOfflineServers`), never
  in the system prompt, so the cached prefix is untouched; `TurnDecoration` renders it first in the
  prefix: `[Offline household servers: printer, vault. Their tools are missing this turn; tell the
  user if the request needs them]`. It persists with the message, so history decorates the same
  bytes every turn; a turn with nothing offline clears it.
- **A call in flight when its tunnel drops** (`QualifiedMcpTool`, on the endpoint's `Offline` — the
  tunnel's `Closed` token): the call's token is linked to it, and whatever the transport throws once
  it is cancelled (the stream's `IOException` wrapped in `HttpRequestException`, the client's
  went-offline refusal, the linked cancellation) becomes `CapabilityError.For(Unavailable, "<name> went
  offline: its tunnel closed while the call was in flight, …")` — `transient_dependency`, with a hint
  to tell the user and that its tools return on a later turn. A call made later in the same turn gets
  the same envelope without being sent.

### Listing a household's connected servers

`docker exec agent dotnet Agent.dll list-household-servers --household <tenantId>` prints each open
tunnel of that household: its name, `connected since <OpenedAt:u>`, its generation, and the tools it
lists **through its tunnel now** (`TunnelListing`, ten seconds at most, "did not answer" past that);
the listing also carries the id of the credential each tunnel was opened with (`ConnectedServer.CredentialId`),
which the owners' servers page marks as in use (`.claude/rules/admin.md`, "Own servers").
The registry lives in the serving process alone, so the command asks it at `GET
/internal/tunnels/{tenantId}` (`ConnectedServers.Path`), under `/internal` (never routed by the
proxy) and behind `AGENTAPI__SHAREDSECRET`, which the command reads from the same environment.
`--agent` defaults to `http://localhost:8080`, the agent inside its own container.

**The key commands reach every household's servers the same way.** `redelegate` and
`retire-signing-keys` ask every channel endpoint for the delegations it keeps
(`.claude/rules/service-identity.md`); one named by its server name — Home Assistant's watches, the
library's downloads, web search's logins — is every household's server of that name, each only inside
its own tunnel. So `CommandServers` asks the agent `GET /internal/tunnels/servers/{name}`
(`ConnectedServers.Holders`), which asks the accounts service who holds one (`POST
/accounts/internal/tunnels/holders`, `TunnelCredentialProtocol.HoldersPath`, the relay's own caller:
every household with a live credential for that name, a suspended one included, and the home whether it
holds one or not, as the channel endpoints always named its servers) and answers each as a
`HouseholdServer` — its id and name, home, suspended, and whether its tunnel is open in the registry. The
source is the credential and not the registry because a server whose tunnel is closed still keeps its
delegations, and only its credential says it exists. Each connected one is dialled at
`/internal/tunnels/{tenantId}/{name}/mcp` (`ConnectedServers.Mcp`, `McpServerEndpoint.ThroughAgent`: that
route, the agent API's secret, calls signed for that household's synthetic address), which
`TunnelForwarder.ForwardAsync` sends down that household's tunnel alone on the `mcp` tag (whose pool
presents the tunnel credential; the command's `Authorization` is withheld), all behind
`AGENTAPI__SHAREDSECRET` like the listing. The command names each server by household — `homeassistant
of household Casa (<id>): asked, 2 delegation(s) kept.` — and one not connected is **not asked**, saying
why (its tunnel is not open, or its household is suspended), so the operator knows whose delegations
want a second pass; an agent or accounts service that cannot say who holds a server leaves that endpoint
`could not be asked`. Either way retiring keys refuses, so retiring wants every holder's kit servers
online, or `--force` (`RedelegationOverTunnelTests`).

## A household's channel server

A server that is also a channel (web search, library, Home Assistant, voice) is named in
`channelEndpoints` **by its server name instead of a URL** — `{ "channelId": "homeassistant",
"endpoint": "homeassistant" }` — and `AddAgent` refuses a channel endpoint that is neither. Such an
endpoint is **every household's server of that name**, each household's tunnel a connection of that
household's (`ChannelHousehold.Own`, `.claude/rules/channels.md`): nothing is configured per household,
so onboarding stays a credential and a kit. Which household a tunnel is, and whether it is the home, the
agent learns from the accounts service, not from a setting: the verify answer carries the credential's
household and `TunnelCredentialVerified.Home`, and the opening keeps both (`OpenTunnel.TenantId`,
`OpenTunnel.Home`).

- `ChannelConnectionHost` runs a URL endpoint once, as before, presenting `McpCredentials.For(url)`
  (the host hands the credential with the endpoint: `IMcpChannelConnection.RunAsync` takes a
  `McpServerEndpoint`). A server-name endpoint it **follows**: `IOwnChannelServers.WatchAsync(name)`
  (the deployment's is `RelayedOwnServers`, over `TunnelRegistry.WatchAsync`) yields every opening of
  any household's tunnel of that name as an `OwnServerOpening` — the `Relayed` endpoint, the opening's
  `Closed` token, its household and whether that is the home. The first opening of a household makes
  that household's connection (`householdConnection`, which `InjectorModule` points at the live
  connection set the monitor and the alert relay watch), and the host runs it once per opening, on a
  token linked to it. A newer opening of the same household's server (a replacement, a reconnect)
  finishes that household's old run before the new one starts, since one connection holds one client;
  no household's opening touches another's run.
- **A run that returns lets go**: `McpChannelConnection.RunAsync` stops its pump and disposes its
  client whenever its token goes, so a channel whose tunnel closed — dropped, its credential revoked,
  its household suspended — is **not connected** (ADR 0011's five behaviours) until the next opening,
  and the next opening starts from nothing — its tool set is asked again (ADR 0012). The connection
  itself stays in the set. A household's channel server is dynamic: away, it is dropped and never fails
  the agent (ADR 0027). The run's registration, health checks and `channel_receive` long-poll are the
  dialled channel's, carried over the tunnel's client and signed for the synthetic address.
- **A message is believed for its connection's household alone.** `ConversationGroup` refuses a
  message on a household's connection whose verified household is another — a person's or the guest's
  assertion, a fire's delegation, or the household its server says it keeps the object under — before
  anything is built (`ChatMonitor.WrongHouseholdErrorType`, `WrongHousehold`, logged and published
  under the connection's household naming both; a person's turn is answered as a refused one). A turn
  acting for nobody that came through a household's server (a download notice, an undelegated watch),
  or a fire whose server names a household it keeps the object under (a schedule whose delegation no
  longer holds), is that household's (`HouseholdChannels.HouseholdOfTurn`): its agent is built for that
  household and it delivers to that household's connections, never the home's. **A conversation is one
  household's** too — the household of the first turn that named one, else the one its topic records
  (`ChatThreadResolver.HouseholdOfAsync`): a message of another household naming it, live or kept, is
  refused as `WrongHousehold` before it joins the group or its history is read. Refused assertions and
  delegations on a household's own server are counted under that household. Otherwise nothing
  about a turn changes: a fire carries the delegation the server stored and is checked as every fire
  is (`.claude/rules/service-identity.md`, "Deferred turns"); a reply goes back through whichever
  opening is current.
- A stack hosting its servers in process names its channels by URL; nothing there follows a tunnel.

Tested at the round trip (`TunnelledChannelTests`: a notification and its reply, a drop and a reopen
without a restart, a watch's delegated fire through the real Home Assistant server;
`HouseholdChannelServersTests`, over `TunnelledAgent` — the real relay, channel host and monitor with
a stand-in model that says whose agent answered: another household answered by its own agent through
its own tunnel, two households' servers of one name each answered alone, a message naming another
household refused, a drop and reopen leaving the other household alone, suspension and revocation)
and in `ChannelConnectionHostTests` (one run per opening, never two at once).

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
never to the web search container (`DeploymentConformanceTests`: no route names it). A link is
`/browse/<household>/<page>/<token>` (`BrowsePagePath`, Domain, the one spelling both ends read), and
`BrowsePages` forwards every exchange under it **unchanged, path included,** to that household's web
search under the `pages` tag (`TunnelProtocol.PagesTag`), which web search registers for its pages
listener (`AddTunnelListener`); the stream lands on that listener's port, so `PagesListener`'s split
holds as before. The link, its claim, the cookie and the live view are the server's: it serves them
under `/browse/{household}` (a route group, `HandoffEndpoints`), scopes both view cookies to
`Path=/browse/<household>` and renders that base into the page, so every request the page makes —
claim, Done, the live view's socket, the logins list and Forget — carries the household too. With
that household's tunnel not open it answers **502 with a page of its own** saying the household's
browser is offline.

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
- **Every request names its household** (`X-Ziggurat-Household`, `RelayedCallProtocol.HouseholdHeader`)
  and reaches that household's server of the name alone (`TunnelForwarder.ForwardAsync`, over
  `TunnelRegistry.Find`) — another household's of the same name is never reached instead, and a
  request naming none is refused 403 like one off the list, never sent to the home's. A caller on the
  list is a shared server, which serves every household and so may act for any of them; it names the
  household from what it holds — the timers server, the household the verified call that armed the
  timer named (`.claude/rules/timers.md`). The relay believes that as it believes the caller's name:
  on its own credential. A proof signed by the agent (the delegation a timer's write could carry) would
  be the stronger check, and is not built.
- **Forwarded on the `mcp` tag**, whose pool presents the household server's tunnel credential
  (`RelayHandler`), with the caller's name, the household it named and the deployment's
  `X-Announce-Token` withheld: nothing of the deployment's reaches a household. A tunnelled server's endpoints beside `/mcp` recognise the
  relay by `RelayCredential` (registered by `AddTunnel` while tunnelled): voice's `VoiceHubAuth` lets
  it through, so the kit's voice never holds the deployment's announce token.
- **Offline is a gateway's answer**: the named household's tunnel not open, or closing under the call,
  is **502 with the reason** in the body. `VoiceHubHttp` reads 502/504 as the hub being unreachable
  (`VoiceHubUnavailableException`), so a timer's create fails with `TransientDependency`, "the voice
  hub is unreachable, so the timer was not armed", and its fire is logged as not rung.

### Which household

A shared server's call names it (above): a timer's ring, dismiss and validation reach the voice server
of the household the timer is for. **A browse link names it too**: web search mints the link where
`web_login`/`web_logins` is called, under the household of the account the call token names
(`ILoginHandoffs.BeginAsync` and `ILoginsPages.MintAsync` take the `Account`) — which is the server's
own household, since a turn reaches only its household's web search. The relay forwards it to that
household's tunnel alone (`TunnelForwarder.ForwardAsync`, over `TunnelRegistry.Find`); none open is
the offline page, and another household's web search — the home's included — is never a fallback.
The view cookies are on `Path=/browse/<household>`, so a browser holding two households' views sends
each server its own key alone. **A link naming no household** — its first segment after `/browse` a
page (`handoff`, `logins`), which no household id (32 lowercase hex) can be — was minted by a web
search from before links named one, when every link was the home's, and still goes to the home's
(`TunnelForwarder.ForwardHomeAsync`), so a kit not yet rebuilt keeps working; a rebuilt server serves
nothing there. Tested at the round trip (`BrowsePagesTests`: another household's link served by its
own web search with the home's connected beside it, the offline page with only the home's connected,
the cookie's path, and a link naming no household reaching the home's).

## Outposts are tunnels too

An outpost (`.claude/rules/outpost.md`) is a server whose credential was minted for `outpost-<NAME>`
(`Domain/Outposts/OutpostServerName.cs`): the prefix on the **verified** name is what tells it apart
from a household's own server, never anything on the open, and it keeps an outpost from replacing an
own server of the same NAME in the registry. `TunnelledOutposts` reads `Open` for a session's live
outposts (each `OpenTunnel.OutpostEndpoint`, `McpServerEndpoint.RelayedOutpost`, dynamic) and consumes `WatchAsync` to record each
opening and closing as an `OutpostEvent`. A consumer of `Open` or `WatchAsync` tells a machine apart with
`OutpostServerName.NameOf`: a channel follows its tunnel by its own server name, so it never takes a
machine for a channel, and the operator's listing (`TunnelListing`) shows a machine as `outpost-<NAME>`
beside the household's servers, deliberately.

## The tenant kit

`TenantKit/docker-compose.yml` is a household's own servers, run at its own site with the images the
deployment builds; `TenantKit/README.md` is how to run it and how the home moved into it. What it holds
to (`TenantKitConformanceTests`, through `DeploymentFiles.KitCompose`):

- **Each server is tunnelled by its environment alone**: `MCP__TUNNEL__HUB` (one value for all,
  `wss://${DEPLOYMENT_HOST}/tunnel`), `MCP__TUNNEL__CREDENTIAL` (`${TUNNEL_CREDENTIAL_<SERVER>}`, one per
  server) and `MCP__TUNNEL__SERVERNAME`. None is told a dialled server's gate — no `MCP__SHAREDSECRET`,
  `MCP__AUDIENCE` or `MCP__AGENTKEYSURL` — and the kit's `.env` shares no line with the deployment's but
  what any site has of its own (paths, ids, the LAN address), the household's own OpenRouter key and
  `LOGINS__STATEKEY`, which the deployment keeps only for the household migration.
- **The names**: `websearch`, `vault`, `library`, `sandbox`, `printer`, `homeassistant`, `voice` — the
  set the shipped definitions and channels name, exactly (a name nothing runs is a server offline
  forever, a context line and nothing louder), with web search and voice under `HouseholdServerNames`.
  One more runs only under the `lemonade` profile and is named by no agent: **`lemonade`**, the
  household's Lemonade forwarder (`LemonadeForwarder/`, service `lemonade-forwarder`). It serves no
  MCP (`AddTunnelledHost`: the tunnel names only its listeners, so the relay's `mcp` tag reaches
  nothing) and makes no back-call; its one listener, under the `chat` tag (`TunnelProtocol.ChatTag`
  — not voice's `lemonade` back-call, which goes the other way to the deployment's own speech box),
  passes every request to the box at `BOX__URL` (the kit's `LEMONADE_URL`, empty refuses its start)
  and adds `BOX__APIKEY`, so the box's key never leaves the house. The agent reaches it through
  `TunnelRegistry.StreamsTo` (`LemonadeForwarderTests`).
  Each name is also a server the test stacks host in its place (`AgentAppSettingsTests`, by
  `McpServerRegistrations`: tools for an agent, a channel for a channel).
- **Back-calls are loopback ports dialled by their client**: voice's `accounts` (`ACCOUNTS__BASEURL`),
  `topics` (`AGENT__BASEURL`), `lemonade` (`Stt__OpenAi__BaseUrl`/`Tts__OpenAi__BaseUrl`, `/v1`) and
  `metrics` (`METRICS__URL`, so voice opens no Redis); web search's `metrics`; the sandbox's
  `vfs-bridge` (`VFSBRIDGEURL`). A server names only the back-calls it makes, and every loopback URL it
  is given is one.
- **The deployment is reached through the tunnels alone**: every address a kit service is told is a kit
  service, its own loopback, the host's gateway (Music Assistant) or the deployment's main host (the
  hub, and web search's `HANDOFF__PUBLICBASEURL`); no network is `external`. The sandbox is on a network
  no other kit service is on and is handed nothing but its tunnel credential.
- **Its own state is its own**: the kit's Redis (`KIT_REDIS_PASSWORD`) holds web search's logins and
  links and the library's routing. Home Assistant's server and voice talk on the kit's network under
  the kit's own `Announce__Token`; the deployment's timers server rings voice through the relay, which
  presents voice's tunnel credential instead.
- **The state move** (`state-move`, profile `move`, `.claude/rules/household-migration.md`) copies the
  home's Redis state out of the deployment's into the kit's, told the deployment's Redis on its own
  command line.

The eval and integration stacks host each named server in process (`InProcessOwnServers`); they name
their channels by URL (the eval none), so `InProcessOwnServers` needs no `IOwnChannelServers`.
