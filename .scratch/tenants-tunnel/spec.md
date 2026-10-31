# Tenants 4a: the tunnel

Status: ready-for-agent

## Problem Statement

Everything Ziggurat can do for a household runs inside one compose file on one machine. The
servers that touch a home — its notes vault, its media library, its printer, its Home Assistant, its
voice satellites, the browser that logs into its accounts, the sandbox that runs code on its GPU —
are deployment containers wired to that machine's mounts, LAN and devices. A second household on
the same deployment could use none of them: there is no way for a server that lives in somebody
else's home to join, and the deployment has no business holding another home's mounts or LAN.

Outposts were the first step towards a server that lives elsewhere, but an outpost only registers
with the stack; the agent then dials it back at the address it advertised. That address must be
reachable from the deployment, which a machine behind a home router is not, and dialling an address
somebody supplied is a door into the deployment's own network for anyone who can register one.

Web search shows the same problem from the other side: every household's browsing would leave from
the deployment's IP address, with one fingerprint, logging into everyone's accounts from one place.

## Solution

A household runs its own servers at its own site, and each of them opens one outbound tunnel to the
deployment. Everything the agent says to that server, and everything the server sends back, travels
inside the tunnel: tool calls, the notifications a channel server long-polls for (a watch firing, a
voice turn, a login wake), the web pages a person opens from a link (the login handoff's live view,
the logins page), and the few requests the server makes back to the deployment (the accounts
service for a speaker, the sandbox's bridge to the other mounts, the metrics feed). The agent never
dials an address a household supplied, and a household's machine needs no open port.

A tunnel proves which household's server it is with a credential the operator mints for that
server. Inside it, every call still carries the agent's signed call token naming the person it acts
for, and the agent's public keys arrive over the tunnel itself.

The operator's own household is the first to run this way. The repo ships a tenant kit — a compose
file with the same images as today — and the home's web search, vault, library, sandbox, printer,
Home Assistant and voice move into it, running beside the deployment on the same machine but
reaching it only through their tunnels, exactly as another household's would. Outposts move onto
the same tunnel. When one of a household's servers is offline, a turn simply goes without its tools
and the agent knows which are missing.

## User Stories

### People

1. As a person, I want my household's assistant to read my notes, play my media and run my lights
   from servers in my own home, so that my home's data never has to live on the deployment.
2. As a person, I want web searches and logins made for me to come from my household's own internet
   connection, so that sites see my home's IP and not a data centre's shared by strangers.
3. As a person, I want a login handoff link to open the live browser view wherever I am, so that I
   can finish a login without my home's machine being reachable from the internet.
4. As a person, I want the logins page to show and forget my remembered logins as before, so that
   moving the browser home changes nothing I do.
5. As a person, I want a watch I set in my Home Assistant to keep firing as me, so that moving Home
   Assistant into my household's site does not silence my automations.
6. As a person, I want to keep talking to my satellites at home, so that voice keeps working after
   the voice server moves into my household's site.
7. As a person, I want my timers to keep ringing on my satellites, so that a kitchen timer works as
   it does today.
8. As a person, I want the assistant to tell me when one of my household's servers is offline,
   rather than failing mysteriously, so that I know to check the machine at home.
9. As a person, I want a turn to keep working with the servers that are online when one is not, so
   that a printer being off does not stop me asking about the weather.
10. As a person, I want a command running in the sandbox to read and write my other mounts as it
    does today, so that the sandbox moving home does not break my scripts.
11. As a person, I want a download I asked for to tell me when it finishes, so that the library
    moving home keeps its notifications.
12. As a person, I want my outpost machine to keep appearing as a mount without opening a port on
    my network, so that I can publish a folder from a laptop behind any router.
13. As a person, I want a call made for me through a household's server to act only for me, so that
    the tunnel does not let one person act as another.

### The operator

14. As the operator, I want to mint a credential for one of a household's servers by name, so that
    the server can join the deployment as that household's server and nothing else.
15. As the operator, I want to revoke a server's credential and have its tunnel closed at once, so
    that a stolen credential stops working without a redeploy.
16. As the operator, I want a credential shown once and stored only as a hash, so that the accounts
    store leaking does not leak working credentials.
17. As the operator, I want the deployment to refuse a tunnel whose credential is unknown, revoked or
    for another server name, with the reason logged and reported on the errors feed, so that I can
    see a misconfigured site.
18. As the operator, I want each of my household's servers to run from the tenant kit beside the
    deployment, so that my own home proves the path every other household will use.
19. As the operator, I want the tenant kit to hold no deployment secret, so that handing the kit to
    another household hands them nothing of mine.
20. As the operator, I want the deployment compose file to no longer carry the home's mounts,
    devices, satellites' addresses, Home Assistant or the browser, so that the deployment is the
    same for every household.
21. As the operator, I want a server on a tunnel to reach back only the few deployment services it
    needs, so that a household's site cannot browse the deployment's internal network.
22. As the operator, I want a server's tunnel to reconnect by itself after the deployment restarts
    or the network drops, so that a deploy does not need anyone at the household's site.
23. As the operator, I want to see which of a household's servers are connected, since when, and
    which tools each offers, so that I can tell a quiet server from a dead one.
24. As the operator, I want the agent's key rotation to reach tunnelled servers without touching
    them, so that rotating keys does not mean visiting every household.
25. As the operator, I want the outpost's shared deployment secret retired, so that one leaked
    value no longer registers outposts for every household.
26. As the operator, I want the migration of today's home state (remembered logins, one-time links,
    download routing, Home Assistant's and voice's volumes) into the kit to be a documented, one-off
    step, so that moving my home onto the tunnel loses nothing.
27. As the operator, I want only one new route through the proxy — the tunnel's — plus the browse
    pages it already serves, so that the attack surface grows by one door.

### Developers

28. As a developer, I want any server built on the hosting library to run behind a tunnel by
    configuration alone, so that making a server a household's server is not a rewrite.
29. As a developer, I want a tunnelled server to keep every gate it has today (its credential, the
    call token check, the pages listener split), so that the tunnel adds a layer and removes none.
30. As a developer, I want the agent to address a household's server by name, resolved against the
    turn's household, so that agent definitions do not hard-code a household.
31. As a developer, I want a tunnelled server addressed by a stable synthetic address that the
    call-token audience, the tool qualifier and the prompt cache all key on, so that nothing that
    keys on a server's host needs to know about tunnels.
32. As a developer, I want a channel server on a tunnel to be connected by the agent when its tunnel
    opens and dropped when it closes, so that long-lived channel connections follow the tunnel.
33. As a developer, I want a call in flight when a tunnel drops to fail with an error that says the
    server went offline, so that the model reports it instead of retrying blindly.
34. As a developer, I want the eval and integration stacks to keep running servers in process
    without a tunnel, so that tests stay fast and the tunnel is tested at its own seam.
35. As a developer, I want voice to mint topics through an agent API instead of writing the
    deployment's Redis, so that a household's voice server needs no deployment database.
36. As a developer, I want a tunnelled server's own state (logins, links, download routing) in the
    kit's own store, so that nothing a household's server keeps lives in the deployment.

## Implementation Decisions

### Two kinds of server

- **Shared deployment servers** run in the deployment and serve every household: scheduling,
  timers, Sugary, Idealista, the chat hub and Telegram. Nothing about them changes in this phase
  (timers' ringing changes in 4b).
- **A household's own servers** run at the household's site and reach the deployment only through
  their tunnels: web search (with its Camoufox), vault, library (with qBittorrent and Jackett),
  sandbox, printer, the Home Assistant server (with Home Assistant and Music Assistant themselves),
  and voice (with its satellites and speaker extraction). The home household is the first.

### The tunnel

- One tunnel per server. A server built on the hosting library, given a hub address and a tunnel
  credential, opens one outbound WebSocket to the deployment's tunnel route through the proxy and
  keeps it open, reconnecting with backoff when it drops. The relay that accepts tunnels lives in the
  agent. The proxy gains one route for it.
- The tunnel carries multiplexed HTTP exchanges in both directions, with streaming bodies and
  WebSocket upgrades. Towards the server: the agent's MCP traffic (streamable HTTP, unchanged), and
  the browse pages a person opens through the proxy, which the proxy routes to the relay and the
  relay forwards down that household's web search tunnel; and a shared server's requests to a
  household's server through the relay's internal endpoint (timers announcing, dismissing and
  resolving satellites on the household's voice server). Towards the deployment: the server's
  back-calls (below). The server side hands each inbound exchange to its own listeners, so its
  existing gates — the per-server credential, the call-token check, the pages-versus-`/mcp` listener
  split — apply unchanged.
- On open, the relay verifies the credential with the accounts service and answers with the
  household's id, the server's name, the server's synthetic address (its call-token audience) and
  the agent's current public keys; it sends the keys again on rotation. The call-token check on a
  tunnelled server verifies against the keys that arrived over its tunnel, never a public route.
- A tunnelled server is addressed by a stable synthetic address derived from the household and the
  server's name. Everything that today keys on a dialled URL's host — the call-token audience, the
  credential lookup, the tool qualifier, the prompt cache, the deployment endpoint list — keys on
  that address. The agent reaches it through an HTTP handler that sends the exchange over the
  relay, never through the network.
- Agent definitions name a household's server by name (a tunnel endpoint, beside the URL endpoints
  of shared servers); the agent resolves it against the turn's household. In this phase every agent
  is the home household's, so the home's agents name its servers.

### Credentials

- A tunnel credential belongs to one household and one server name. The accounts service mints it
  (an operator command in this phase; the owner's admin area in 4c), shows it once, stores only its
  hash, and revokes it; revoking closes that server's open tunnel. The relay asks the accounts
  service's internal API to verify a credential on every open.
- A refused tunnel (unknown, revoked, another server's name) is closed with a reason, logged and
  reported on the errors feed.
- The outposts' shared deployment secret is retired: an outpost is a server with its own credential
  like any other.

### Back-calls

- A server on a tunnel keeps its own state at its site: the kit ships its own Redis for web search's
  remembered logins and one-time links and the library's download routing. The sealing key for
  remembered logins is a kit secret.
- What must stay deployment-side is reached back over the same tunnel, to an allowlist the relay
  enforces and nothing else: the accounts service's resolution and guest endpoints (voice), the
  agent's VFS bridge (the sandbox), the metrics feed, the shared Lemonade (voice's speech-to-text
  and text-to-speech), and a new agent API for minting topics (voice). Each back-call carries the
  tunnel's identity, so the deployment knows which household's server is asking.
- Voice stops writing the deployment's Redis: the topics its turns need are minted by the agent
  through that API, for every voice server, tunnelled or not.

### Channels and turns

- A tunnelled server that is also a channel (web search, library, Home Assistant, voice) is
  connected by the agent when its tunnel opens and dropped when it closes, with the same
  registration, health checks and long-poll as a dialled channel today.
- A conversation's tool sessions are reconciled at each turn's start: a household's server that came
  online since joins, one that went offline leaves, and one line of the turn's context names the
  household's servers that are offline, so the agent can say so. A call in flight when its tunnel
  drops fails with an error saying the server went offline.

### Outposts

- The outpost binary uses the same tunnel code: it opens a tunnel with its own credential and serves
  its filesystem over it; the agent no longer dials it back. It keeps its flags, its `outpost:<NAME>`
  mount and its jail and exec rules; `--advertise` and `--port` go. It now checks call tokens with
  the keys its tunnel delivers. Its registration and keepalive become the tunnel's open and
  liveness; an outpost appears while its tunnel is open.

### The tenant kit

- The repo ships a tenant kit: a compose file and its docs, with the same images the deployment
  uses, each server configured by its environment and holding its own tunnel credential, plus the
  kit's own Redis. It holds no deployment secret.
- The home household runs the kit beside the deployment on the same machine, on networks of its own,
  so its servers reach the deployment only through their tunnels. The deployment compose file loses
  those servers, their mounts and devices, Home Assistant, Music Assistant, qBittorrent, Jackett,
  Camoufox and speech extraction, and the per-server secrets of the servers that moved.
- Moving today's home state into the kit — remembered logins, one-time links and download routing
  out of the deployment's Redis, and the volumes of Home Assistant, voice, the library and the
  browser — is a one-off documented step with a command the kit provides.

## Testing Decisions

A good test checks what a caller observes: whether a tunnel opens or is refused and why, what a call
through it returns, which tools a turn offers, what a person's browser gets from a link. It never
checks the tunnel's framing beyond what an endpoint sees.

Five seams, one of them new:

1. **Tunnel round-trip (new).** A real server host connected to a real relay in process: MCP calls,
   `channel_receive` notifications, the browse pages and their live-view WebSocket, back-calls
   allowed and refused, a bad or revoked credential refused, reconnect after the relay restarts, the
   agent's keys delivered over the tunnel and used by the call-token check.
2. **The agent building a turn (existing).** With tunnelled servers: an offline server's tools
   omitted and named, a server that comes online joins the next turn, a call in flight when the
   tunnel drops fails clearly. Prior art: the `ThreadSession`/`McpClientManager` tests.
3. **The outpost binary (existing).** On the tunnel: it mounts, jails and executes as before, and is
   gone when its tunnel closes. Prior art: the outpost tests.
4. **Deployment files (existing).** The kit's compose holds no deployment secret, the proxy routes
   only the tunnel's path and the browse pages to the agent, and no agent setting names a
   household's address. Prior art: `DeploymentConformanceTests`.
5. **The smoke tier (existing).** The home kit beside the smoke stack: a signed-in reply that uses a
   tunnelled server, a browse page through the tunnel, a forged tunnel credential refused. Prior
   art: `StackSmokeTests`.

The eval and integration stacks keep hosting servers in process and resolve tunnel endpoints to
them directly.

## Out of Scope

- Per-household configuration, keys, agents, stores and migration of shared stores (4b), and the
  admin area and onboarding (4c). In this phase the only household with servers is the home one.
- Timers ringing on channels (4b): the home's timers keep ringing on its satellites through its
  voice server.
- A hosted per-household blob store.
- A household bringing its own Lemonade.

## Further Notes

- Phase 4a of the multi-tenant plan ("Tenants decisions"). It branches from `web-identity` and
  merges back into it. The glossary's Households section names its terms: own server, shared
  server, tunnel, relay, back-call, tenant kit.
- ADRs it touches:
  - *Static endpoints fail, dynamic ones are dropped* (0027): a household's own server named by an
    agent is configured, yet an offline one no longer fails the session; it is left out of the turn
    and named in its context. That is a deliberate change for tunnel endpoints only; URL endpoints
    of shared servers keep failing as before.
  - *A server's tool set is fixed for a connection generation* (0012): each opening of a tunnel is
    a new generation; the reconciliation at a turn's start is where a new generation's tools enter.
  - *A subagent inherits outposts only if it also asks* (0028): unchanged; an outpost on the tunnel
    is still an outpost.
  - *A watch is a home automation and the home carries its fires back* (0038): unchanged in spirit;
    the home is now the household's site and its fires come back through the tunnel.
- The glossary's outpost entries change with this phase: an outpost registration becomes its open
  tunnel.
- Deploying it is a move of the home: mint a credential per home server, start the kit, run the
  state move, remove the moved services from the deployment, and reload the proxy for the tunnel
  route. Outposts need a new credential each and a restart without `--advertise`.
