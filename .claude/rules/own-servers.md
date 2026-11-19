---
paths:
  - "Infrastructure/Agents/IOwnServers.cs"
  - "Infrastructure/Agents/IOwnChannelServers.cs"
  - "Infrastructure/Agents/OwnServerEndpoints.cs"
  - "Infrastructure/Agents/McpServerEndpoint.cs"
  - "Infrastructure/Agents/McpAgent.cs"
  - "Infrastructure/Agents/Mcp/QualifiedMcpTool.cs"
  - "Infrastructure/Tunnels/RelayedOwnServers.cs"
  - "Infrastructure/Tunnels/TunnelListing.cs"
  - "Agent/App/ChannelConnectionHost.cs"
  - "Agent/App/TunnelApi.cs"
  - "Agent/Commands/AgentCommand.cs"
  - "Domain/DTOs/Tunnel/ConnectedServers.cs"
  - "Domain/DTOs/Tunnel/TunnelCredentials.cs"
  - "Tests/InProcessOwnServers.cs"
  - "Tests/Integration/Tunnels/HouseholdChannelServersTests.cs"
  - "Tests/Integration/Tunnels/TunnelledChannelTests.cs"
  - "Tests/Integration/Tunnels/TunnelledAgent.cs"
  - "Tests/Integration/Tunnels/TunnelledHomeAssistant.cs"
  - "Tests/Integration/Tunnels/RedelegationOverTunnelTests.cs"
  - "Domain/Agents/ChatThreadResolver.cs"
---

# A household's own servers in the agent

See also: `tunnel.md` (wire, opening, registry, back-calls), `tunnel-forwarding.md`, `tenant-kit.md`.

## Reaching a household's server from a turn

- **An agent definition names a household's server by its name** — a bare DNS label
  (`TunnelCredentialProtocol.IsServerName`) — beside shared servers' URLs: `["vault",
  "http://mcp-scheduling:8080/mcp"]`. `AddAgent` refuses an endpoint that is neither, and
  `ServerAddress.FromUrl` refuses anything not http(s) with a host, so the two never confuse.
- The projection makes it `McpServerEndpoint.OwnServer(name)` (`NamesOwnServer`, no credential).
  Every reader of a configured endpoint (projection, channel host, key commands) goes through
  `McpServerEndpoint.Of(address, credential)`, and every dial through `McpClientManager.TransportFor`
  (`McpEndpointTransport` outside Infrastructure): nothing else tests `IsServerName` on an endpoint or
  presents a credential by hand.
- The household travels as data: `ConversationGroup` passes the verified identity's tenant (a
  person's or **the guest's**) to `IAgentFactory.Create(..., tenantId)` → `AgentSpec.TenantId`; a
  worker takes its parent's via `SpawnContext.TenantId`.
- **Resolved at every turn's start** (`OwnServerEndpoints`, beside the outposts in
  `McpAgent.GetOrCreateSessionAsync`), in definition order, through **`IOwnServers`**. The
  deployment's is `RelayedOwnServers`: the open tunnel's `OpenTunnel.Endpoint`
  (`McpServerEndpoint.Relayed`), bound to that generation. In-process stacks register their own — the
  eval (`EvalStack.OwnServers`: a name stands for compose server `mcp-<name>`) and integration tests
  (`Tests/InProcessOwnServers.cs`) — answering the hosted URL; **no tunnel runs in either**.
- A registered agent may name a household server too: the name cannot point anywhere but the turn's
  own household's server, and the call token still names the person.

### The synthetic address

`ServerAddress.Tunnelled(household, name)` (keys generally: mcp-hosting.md): `Name` =
`<name>.<household>.tunnel` — the call token's audience, never resolved; `Qualifier` = the bare name,
so tools are `mcp__vault__…` in every household and whitelists read the same; `CacheKey` = the full
name, so the per-process prompt cache never serves one household's server prompts to another.

## Offline servers

**An exception to ADR 0027, for tunnel endpoints only** (the ADR itself is not edited; this is where
the exception is recorded). A household server named in a definition is `Configured`, yet one not
connected **does not fail the session**: it is left out of the turn and named in its context — a
machine at home being off is not the deployment being broken. Shared servers' URL endpoints keep ADR
0027: retried, then fatal.

- **Reconciled at each turn's start.** `OwnServerEndpoints.Resolve` answers an
  `OwnServerResolution`: endpoints to dial (unreachable ones left out), the names left out, and one
  `ServerState(name, online, generation, withheld)` per named server.
  - A server withheld by name (household never set it up, or a worker's parent does not name it —
    `.claude/rules/household-config.md`, "The agents a household runs") is neither dialled nor on the
    offline line; its state says so, and a turn after setup builds a new session that reaches it.
  - `McpAgent` keeps the states beside the session and compares with the next turn's
    (`ReachesTheSameServersAs`): equal reuses the session; different — online, offline, or
    **reconnected as a new tunnel generation** — disposes it and builds a new one, outposts and shared
    servers redialled. An agent using outposts (`usesOutposts`, a worker's ANDed with its parent's, ADR
    0028) compares machines the same way (`ComposedEndpoints.ReachesTheSameMachinesAs`: each live
    outpost's name and generation as `ServerState`s), so a machine opening, closing or reopening joins
    or leaves at the next turn; one that does not never asks.
  - That is how a new generation's tools enter (ADR 0012): only at a turn's start, never under a
    running turn. A conversation group's turns run one at a time, so the replaced session has no call
    in flight. In-process stacks have no generations (`InProcessOwnServers` answers null): presence
    alone is the key.
- **Unreachable** is no tunnel of that name open for the household, or a turn naming no household
  (the relay answers null for both). A tunnel closing between lookup and dial is dialled once, never
  retried, and dropped like an offline one (`McpClientManager`, on `McpServerEndpoint.Offline`
  cancelled); failing any other way still fails the session.
- **The context line** is stamped on the turn's own user message as data (`SetOfflineServers`), never
  in the system prompt, so the cached prefix is untouched; `TurnDecoration` renders it first:
  `[Offline household servers: printer, vault. Their tools are missing this turn; tell the user if the
  request needs them]`. It persists with the message so history decorates the same bytes; a turn with
  nothing offline clears it.
- **A call in flight when its tunnel drops** (`QualifiedMcpTool`, on the endpoint's `Offline` = the
  tunnel's `Closed`): the call's token is linked to it, and whatever the transport throws once
  cancelled becomes `CapabilityError.For(Unavailable, "<name> went offline: its tunnel closed while the
  call was in flight, …")` — `transient_dependency`, hinting to tell the user and that tools return on a
  later turn. A later call in the same turn is not sent: `"<name> is offline: its tunnel closed before
  this call, so the call was not sent and nothing was done"`, or, when a newer tunnel opened since
  (`McpServerEndpoint.Reconnected`, `TunnelRegistry.ReopenedSince`; e.g. an approval wait across a
  reconnect), `"<name> reconnected after this turn began, on a new connection this turn cannot use, so
  this call was not sent"` hinting that asking again works. Every hint says the model cannot retry by
  itself after its reply (user's decision, pass 19: the model promised retries it could not make).

### Listing a household's connected servers

`docker exec agent dotnet Agent.dll list-household-servers --household <tenantId>` prints each open
tunnel: name, `connected since <OpenedAt:u>`, generation, and the tools it lists **through its tunnel
now** (`TunnelListing`, ten seconds at most, "did not answer" past that); the listing also carries
each tunnel's credential id (`ConnectedServer.CredentialId`), which the owners' servers page marks in
use (`.claude/rules/admin-owner-pages.md`, "Own servers"). The registry lives in the serving process alone, so the
command asks `GET /internal/tunnels/{tenantId}` (`ConnectedServers.Path`; `/internal` is never routed
by the proxy) behind `AGENTAPI__SHAREDSECRET`, read from the same environment. `--agent` defaults to
`http://localhost:8080`.

**The key commands reach every household's servers the same way.** `redelegate` and
`retire-signing-keys` ask every channel endpoint for its delegations (`.claude/rules/delegations.md`);
one named by server name is every household's server of that name, each only inside its own tunnel.

- `CommandServers` asks the agent `GET /internal/tunnels/servers/{name}` (`ConnectedServers.Holders`),
  which asks the accounts service who holds one (`TunnelCredentialProtocol.HoldersPath`; answer shape in
  tunnel-credentials.md) and answers each as a `HouseholdServer` — id, name, home, suspended, tunnel open. The
  source is the credential, not the registry, because a server whose tunnel is closed still keeps its
  delegations, and only its credential says it exists.
- Each connected one is dialled at `/internal/tunnels/{tenantId}/{name}/mcp` (`ConnectedServers.Mcp`,
  `McpServerEndpoint.ThroughAgent`: agent API secret, calls signed for that household's synthetic
  address); `TunnelForwarder.ForwardAsync` sends it down that household's tunnel alone on the `mcp` tag
  (the pool presents the tunnel credential; the command's `Authorization` is withheld).
- Output names each by household — `homeassistant of household Casa (<id>): asked, 2 delegation(s) kept.`
  One not connected is **not asked**, saying why (tunnel not open, or household suspended), so the
  operator knows whose delegations want a second pass; an
  agent or accounts service that cannot say who holds a server leaves that endpoint
  `could not be asked`. Either way retiring keys refuses, so retiring wants every holder's kit servers online, or
  `--force` (`RedelegationOverTunnelTests`).

## A household's channel server

A server that is also a channel (web search, library, Home Assistant, voice) is named in
`channelEndpoints` **by its server name instead of a URL** — `{ "channelId": "homeassistant",
"endpoint": "homeassistant" }`; `AddAgent` refuses a channel endpoint that is neither. Such an endpoint
is **every household's server of that name**, each household's tunnel a connection of that household's
(`ChannelHousehold.Own`, `.claude/rules/channels.md`): nothing is configured per household, so
onboarding stays a credential and a kit. Which household a tunnel is, and whether it is the home, comes
from the accounts service, not a setting: `TunnelCredentialVerified.Home`, kept as `OpenTunnel.TenantId`
and `OpenTunnel.Home`.

- `ChannelConnectionHost` runs a URL endpoint once, presenting `McpCredentials.For(url)`
  (`IMcpChannelConnection.RunAsync` takes a `McpServerEndpoint`). A server-name endpoint it **follows**:
  `IOwnChannelServers.WatchAsync(name)` (deployment: `RelayedOwnServers` over
  `TunnelRegistry.WatchAsync`) yields each opening as an `OwnServerOpening` (`Relayed` endpoint, `Closed`
  token, household, is-home). A household's first opening makes its connection
  (`householdConnection`, which `InjectorModule` points at the live connection set), run once per
  opening on a linked token. A newer opening of the same household's server finishes the old run before
  the new one starts (one connection holds one client); no household's opening touches another's run.
- **A run that returns lets go**: `McpChannelConnection.RunAsync` stops its pump and disposes its client
  when its token goes, so a channel whose tunnel closed (dropped, revoked, suspended) is **not
  connected** (ADR 0011) until the next opening, which starts from nothing — tool set asked again (ADR
  0012). The connection stays in the set. Away, it is dropped and never fails the agent (ADR 0027).
  Registration, health checks and the `channel_receive` long-poll are the dialled channel's, over the
  tunnel's client and signed for the synthetic address.
- **A message is believed for its connection's household alone.** `ConversationGroup` refuses a message
  on a household's connection whose verified household is another — a person's or guest's assertion, a
  fire's delegation, or the household its server says it keeps the object under — before anything is
  built (`ChatMonitor.WrongHouseholdErrorType`, `WrongHousehold`, logged and published under the
  connection's household naming both; a person's turn is answered as refused).
  - A turn acting for nobody through a household's server (a download notice, an undelegated watch),
    or a fire whose server names a household it keeps the object under, is that household's
    (`HouseholdChannels.HouseholdOfTurn`): its agent is built for, and delivers to, that household,
    never the home.
  - **A conversation is one household's** too — the first naming turn's, else its topic's
    (`ChatThreadResolver.HouseholdOfAsync`): another household's message naming it is refused as
    `WrongHousehold` before it joins the group or its history is read.
  - Refused assertions and delegations on a household's own server are counted under that household.
    Otherwise nothing changes: a fire is checked as every fire is (`.claude/rules/delegations.md`,
    "Deferred turns"); a reply goes through whichever opening is current.
- A stack hosting servers in process names channels by URL (the eval none); nothing there follows a
  tunnel, so `InProcessOwnServers` needs no `IOwnChannelServers`.

Tested at the round trip in `TunnelledChannelTests` and `HouseholdChannelServersTests` (over
`TunnelledAgent`: the real relay, channel host and monitor), and in `ChannelConnectionHostTests` (one
run per opening, never two at once).
