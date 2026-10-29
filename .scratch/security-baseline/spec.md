# Security baseline

Status: ready-for-agent

## Problem Statement

The deployment's only perimeter is "you have to be on the VPN", and inside it almost everything is
open.

- **Ports.** Redis holds every conversation, memory and push subscription, and it is published to
  the whole network with no password. So are the hub, the agent, the dashboard, Camoufox and every
  MCP server, which lets a request bypass the reverse proxy and its TLS entirely.
- **Secrets.** Seventeen services receive the deployment's whole secrets file, so the chat client's
  host and the dashboard hold the OpenRouter key, the Home Assistant token and the key that seals
  people's remembered logins. The whole user-secrets directory, every project's secrets, is
  mounted into fourteen containers.
- **MCP.** One MCP secret opens all fourteen servers, and the sandbox, which runs whatever the model
  writes, holds it.
- **Browsers and the hub.** Both CORS policies accept any origin with credentials. The hub answers
  calls from a connection that never registered, including approval answers, and an approval id
  is eight hex characters.
- **Telegram.** The bot admits a sender whose username, or failing that whose first name, is on a
  list, and any tap on an approval button counts, whoever taps it.
- **Defaults.** The tracked secrets file ships a working announce token.

None of this depends on tenants or accounts, and every later phase of the multi-tenant work
assumes it is closed.

## Solution

- **Ports.** Only the reverse proxy faces the network, plus the operator's own LAN tools, which
  listen on the LAN address the operator names. The ports used for debugging from the host move to
  an opt-in loopback-only override. Redis needs a password and listens on loopback, read from
  elsewhere over an SSH tunnel.
- **Secrets.** Each service is handed, by name, only the secrets it reads, and mounts only its own
  project's user secrets.
- **MCP.** Each MCP server has its own credential, and the agent presents each server its own, so
  the sandbox's credential opens nothing but the sandbox.
- **Browsers and the hub.** CORS admits only the deployment's own origins. The hub refuses every
  call from a connection that hasn't registered, and approval ids can't be guessed.
- **Telegram.** The bot admits numeric user ids only, and an approval tap counts only from the
  person whose turn asked.
- **Defaults and outposts.** No secret has a working default. Outposts register through the
  reverse proxy over TLS.

Making the chat client's user real, and authorizing hub calls per person, belongs to the Accounts
phase. Until then the user is still whatever the browser asserts, so a per-person check here would
mean nothing.

## User Stories

### The network

1. As the operator, I want only the reverse proxy published to the network, so that every user-facing request passes through TLS and through one place where sign-in can later stand.
2. As the operator, I want the MCP servers, channel servers, agent, chat-client host, dashboard, Camoufox and the speech-extraction sidecar reachable only on the deployment's internal network, so that nothing bypasses the proxy.
3. As the operator, I want qBittorrent, filebrowser, Jackett, Home Assistant and Lemonade to listen on the LAN address I name, so that my own tools keep working on the LAN without listening on every interface.
4. As the operator, I want that address to fall back to loopback when I haven't named one, so that a fresh deployment exposes nothing by accident.
5. As someone running the speech typist on a Windows machine, I want Lemonade still reachable on the LAN, so that dictation into my own windows keeps working.
6. As the operator, I want qBittorrent's peer port still reachable, so that downloads keep working.
7. As a developer, I want an opt-in override that publishes the debugging ports on loopback at today's numbers, so that running a project on the host still reaches the containers.
8. As a developer, I want the editor's debug tasks to bring the stack up with that override, so that debugging keeps working without remembering a flag.
9. As the operator, I want an outpost to register through the reverse proxy over TLS, so that its secret never crosses the LAN in plain HTTP and the agent's own port can close.
10. As the operator, I want the agent's dial-back to an outpost to keep working as today, so that outposts need nothing new beyond their hub address.
11. As the operator, I want the exec bridge still unreachable through the proxy, so that opening a route for outposts opens nothing else.

### Redis

12. As the operator, I want Redis to require a password, so that a process on the network can't read every conversation and memory.
13. As the operator, I want Redis published on loopback only, so that I can still read prod over `ssh -L` and nobody else can reach it.
14. As the operator, I want a service with a wrong Redis password to fail at startup with a message that names Redis authentication, so that a misconfiguration is obvious.
15. As the operator, I want Redis's health check to authenticate, so that a password doesn't make the container look unhealthy.

### Secrets

16. As the operator, I want each service to receive only the secrets it reads, so that a compromised chat-client host or dashboard doesn't hand over the model key, the Home Assistant token or the logins key.
17. As the operator, I want each service to mount only its own project's user secrets, so that one container can't read every other project's.
18. As the operator, I want every placeholder in the tracked secrets file used by at least one service, so that the file stays an honest list of what the deployment needs.
19. As the operator, I want no secret to have a working default in the tracked secrets file, so that a forgotten setting refuses instead of accepting a value anyone can read in git.
20. As the operator, I want the announce token empty until I set it, so that nobody can make my speakers talk with a value from the repository.

### MCP credentials

21. As the operator, I want every MCP server and channel server to have its own credential, so that holding one server's credential opens no other.
22. As the operator, I want the sandbox to hold only its own credential, so that code the model writes can't call any other server.
23. As the operator, I want the agent to present each server the credential for that server, so that the agent still reaches everything it is configured for.
24. As the operator, I want the agent to refuse to start when a server it is configured for has no credential, so that a missing secret is caught at startup rather than as a refused call.
25. As the operator, I want a server the deployment doesn't name, such as a registered agent's own endpoint, still dialled without any credential, so that no deployment credential leaks to a third party.

### Browsers and the hub

26. As the operator, I want CORS to admit only my own origins, so that a page elsewhere can't drive the hub or the agent registration API with a visitor's credentials.
27. As the operator, I want CORS to mean same-origin only when no origin is configured, so that an empty setting is the safe one.
28. As a person, I want a hub connection that never registered to be refused on every call, so that it can't read history, change topics or answer approvals.
29. As a person, I want approval ids too long to guess, so that nobody answers my approval by trying ids.

### Telegram

30. As a person on Telegram, I want the bot to admit me by my numeric Telegram id, so that someone who takes my old username doesn't get in.
31. As a person on Telegram, I want someone who merely shares my first name to be refused, so that a missing username is no way in.
32. As a person on Telegram, I want only my own tap to answer an approval my turn asked for, so that someone else in a group can't approve it for me.
33. As someone tapping another person's approval, I want to be told it isn't mine, so that I understand why nothing happened.
34. As a person in a chat where a schedule's fire asks for approval, I want any allowlisted person there to be able to answer it, so that a fire with nobody to ask isn't stuck.
35. As the operator, I want the bot's startup log to list the admitted ids, so that I can check the allowlist after a deploy.

## Implementation Decisions

### Published ports

- The base deployment publishes:
  - the reverse proxy's 443;
  - Redis on loopback;
  - the LAN services (qBittorrent's UI and peer port, filebrowser, Jackett, Home Assistant,
    Lemonade) on a per-deployment LAN bind address.
- The bind address is a non-generic parameter: set in the compose environment, falling back to
  loopback, with a placeholder in the tracked secrets file. Prod sets its LAN address.
- Every other service is reachable only on the deployment's internal network.
- A new opt-in compose override publishes the debugging ports (MCP servers, channel servers, agent,
  chat-client host, dashboard, Camoufox, the speech-extraction sidecar) on loopback at today's port
  numbers. The editor debug tasks and the stack-launch instructions include it.

### Outposts

- The main host gets a route that sends outpost registration to the agent.
- The documented hub address for an outpost becomes the proxy's HTTPS URL.
- The agent's dial-back to a machine is unchanged, and the exec bridge stays unrouted.

### Redis

- One deployment password, a new secret.
- The Redis container requires it and its health check authenticates.
- Every service that reads Redis is handed a connection string carrying the password in its own
  environment.
- No per-service ACL users.

### Per-service environment

- No service receives the whole secrets file. Each service's environment names the variables it
  reads.
- The tracked secrets file stays a list of placeholders, all of them empty.
- The OS overrides mount each project's own user-secrets directory instead of the whole tree. The
  agent and the dashboard share a user-secrets id and so share one mount.

### Per-server MCP credentials

- Each MCP server and channel server keeps reading the same setting for its credential. The
  deployment feeds each one from its own secret variable.
- The agent's single shared secret becomes a map from endpoint host to credential.
  - Each configured endpoint and each channel connection is presented the credential for its host.
  - An endpoint the deployment doesn't name is dialled with none, as today.
  - A configured host with no credential stops the agent at startup.
- The outpost secret is untouched.

### CORS

- The agent and the SignalR channel server read their allowed origins from configuration, a
  per-deployment value in the compose environment.
- Only listed origins get CORS headers, with credentials. An empty list means same-origin only.

### Hub registration and approval ids

- Every hub method except registration and the ping a reconnecting client probes with refuses a
  connection that hasn't registered. The chat client makes no other call until the hub has
  answered its registration, on start and after every reconnect.
- Approval ids are a full 128-bit GUID on SignalR and Telegram. Telegram's callback data still fits
  its 64-byte limit with the prefix.

### Telegram ids

- The username allowlist becomes a numeric user-id allowlist.
- A message is admitted only when its sender's id is listed. The chat-username and first-name
  fallbacks go.
- The turn's sender stays the username, so memory and the identity map keep their keys until
  Accounts moves them to user ids.
- The bot records the numeric id of the person whose message started each turn. An approval tap
  counts only from that id, and anyone else is told the approval isn't theirs.
- An approval raised in a turn that names a Telegram person — a login wake continuing their turn
  included — is theirs alone. One raised by a fire that names nobody is answered by any
  allowlisted person in the chat.

### No working defaults

- The announce token's placeholder becomes empty. The voice hub and the Home Assistant watch
  endpoint already refuse every call while it is unset.

## Testing Decisions

A good test asserts what a caller observes: what a request is answered, what a client is refused,
what the deployment files declare. It never asserts which internal method ran.

Four seams, one of them new.

1. **Deployment conformance (new).** Reads the compose files and the Caddyfile as text and asserts:
   - no service receives the whole secrets file;
   - no base-file port is published without a bind address, except the proxy's 443;
   - Redis is bound to loopback and requires a password;
   - every service that depends on Redis gets a connection string;
   - every MCP server is fed its own credential variable, and no two share one;
   - the sandbox's environment names nothing but its own credential;
   - only the services that read them get the OpenRouter key, the Home Assistant token, the logins
     key or the Telegram tokens;
   - every placeholder is used by a service;
   - each override mounts only its project's user secrets;
   - the debug override publishes only on loopback;
   - the outpost route reaches the agent and the bridge is still unrouted.

   Prior art: `VfsBridgeApiTests.ThePublicProxy_DoesNotRouteTheBridge`, which already reads the
   Caddyfile.
2. **Host-level tests (existing).**
   - The agent spec projection hands each endpoint its host's credential, and channel connections
     likewise.
   - A real server refuses another server's credential.
   - A CORS preflight from a listed origin is answered and from a foreign one is not.
   - An unregistered hub connection is refused on history, topic and approval calls.
   - Approval ids are 32 hex characters.

   Prior art: `AgentSpecProjectionTests`, `McpSecretGateTests`, `ChatHubRegistrationTests`,
   `ChatHubTopicTests`, `ApprovalServiceTests`.
3. **Telegram with a fake bot client (existing).**
   - An allowlisted id is admitted.
   - A non-listed id whose username or first name matches an old entry is refused.
   - Another person's tap leaves the approval pending and is told it isn't theirs; the asker's tap
     resolves it.
   - A fire's approval is answered by an allowlisted tap.

   Prior art: `TelegramBotServiceTests`, `ApprovalCallbackRouterTests`.
4. **The stacks that start containers (existing).** The chat-client E2E stack, the integration
   fixture for jonas's servers and the eval's stack wire their containers themselves rather than
   from the compose file.
   - They give every server its own credential and the agent the matching map, and their suites
     pass unchanged.
   - Their own Redis may stay passwordless, but one runs with a password so the connection-string
     wiring is exercised.

   Prior art: `WebChatStack`, `JonasMcpStackFixture`, `OutpostSecretDialTests`.

## Out of Scope

- A real chat-client user, per-person hub authorization, and sign-in for the dashboard and Sugary:
  the Accounts phase.
- Signed identity in `_meta`: the Service identity phase. Here a server still believes any `_meta`
  from a caller holding its credential.
- Redis ACL users per service, TLS between containers, and encrypting Redis data.
- Lemonade's own authentication. The speech typist reaches it on the LAN; whether the box checks a
  key is the box's own configuration.

## Further Notes

- This is phase 1 of the multi-tenant plan (`.scratch/multi-tenant/plan.md`). It branches from
  `web-identity` and merges back into it; master gets every phase together.
- Deploying needs the operator to:
  - add the LAN bind address, the Redis password and every per-server MCP credential to prod's
    secrets file (the allowed origins are the deployment's own hostnames, written in the compose
    file beside the Caddyfile's);
  - set the announce token on both the voice hub and Home Assistant's REST command;
  - re-point each outpost's hub address at the proxy;
  - read Redis through an SSH tunnel to the prod host from then on.
