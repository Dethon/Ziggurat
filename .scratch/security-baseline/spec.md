# Security baseline

Status: ready-for-agent

Phase 1 of `.scratch/multi-tenant/plan.md`. Branches from `web-identity`, merges back into it.

## Problem Statement

The deployment's only perimeter is "you have to be on the VPN". Inside it, almost everything is
open. Redis, holding every conversation, memory and push subscription, is published on `0.0.0.0`
with no password, and so are the hub, the agent, the dashboard, Camoufox and every MCP server,
which bypasses Caddy and TLS completely. Seventeen services get the whole `.env` through
`env_file`, so the WebChat host and the dashboard hold the OpenRouter key, the Home Assistant token
and the key that seals people's web logins. The whole user-secrets directory, every project's
secrets, is mounted into fourteen containers.

One MCP secret opens all fourteen servers, and the sandbox, which runs whatever the model writes,
holds it. Both CORS policies allow any origin with credentials. The hub answers approval responses
from a connection that never registered, and an approval id is eight hex characters. Telegram lets
in a sender whose username, or failing that whose first name, matches the allowlist, and a tap on
an approval button is honored whoever taps it. The tracked `.env` ships a working announce token,
`announce_secret`.

None of this depends on tenants or real accounts, and every later phase builds on it being closed.

## Solution

Only Caddy faces the network, plus the operator's own LAN tools bound to the LAN interface. Debug
ports move to an opt-in override on loopback. Redis needs a password. Each service is handed, by
name, only the secrets it reads, and only its own project's user secrets. Each MCP server has its
own credential, and the agent presents each server its own, so the sandbox's credential opens
nothing but the sandbox. CORS allows only the deployment's own origins. The hub refuses every call
from a connection that has not registered, and approval ids are unguessable. Telegram admits
numeric user ids only, and an approval tap counts only from the person the turn belongs to. No
secret has a working default.

Outposts register through Caddy on TLS instead of the agent's plain port.

Making the WebChat user real, and authorizing hub calls per user, is Accounts' job: here the user is
still client-asserted, so a per-user check would mean nothing.

## User Stories

1. As the operator, I want only Caddy published to the network, so that every user-facing request goes through TLS and one place I can put auth in front of.
2. As the operator, I want qBittorrent, filebrowser, Jackett, Home Assistant and Lemonade bound to the LAN interface I name, so that my own tools keep working on the LAN without listening on every interface.
3. As the operator, I want an opt-in override that publishes the debug ports on loopback, so that running a project on the host for debugging still reaches the containers.
4. As the operator, I want prod Redis published on loopback only and password-protected, so that I can still read it over `ssh -L` and nobody else on the network can.
5. As the operator, I want each service to receive only the secrets it reads, so that a compromised WebChat host or dashboard doesn't hand over the model key, the HA token or the logins key.
6. As the operator, I want each service to mount only its own project's user secrets, so that one container can't read every other project's.
7. As the operator, I want every MCP server to have its own credential, so that holding one server's secret, the sandbox's included, opens no other server.
8. As the operator, I want CORS to allow only my own origins, so that a page elsewhere can't drive the hub or the agent API with a visitor's credentials.
9. As a person, I want a hub connection that never registered to be refused, so that it can't read history, change topics or answer my approvals.
10. As a person, I want approval ids that can't be guessed, so that nobody answers my approval by trying ids.
11. As a person on Telegram, I want the bot to admit me by my numeric Telegram id, so that someone who takes my old username or shares my first name doesn't get in.
12. As a person on Telegram, I want only my own tap to answer an approval my turn asked for, so that someone else in a group chat can't approve it for me.
13. As the operator, I want no secret to have a working default in the tracked `.env`, so that a forgotten setting refuses instead of accepting a value everyone can read in git.
14. As the operator, I want outposts to register over TLS through Caddy, so that the outpost secret never crosses the LAN in plain HTTP and the agent's port can close.

## Implementation Decisions

- **Published ports.** The base `docker-compose.yml` publishes Caddy's 443, Redis on
  `127.0.0.1:6379`, and the LAN services (qBittorrent's UI and peer port, filebrowser, Jackett,
  Home Assistant, Lemonade) on `${LAN_BIND_ADDRESS}`. `LAN_BIND_ADDRESS` is a per-deployment value:
  it goes in the compose file with `127.0.0.1` as the fallback and a placeholder in `.env`. Prod
  sets its LAN address. Everything else is reached on the compose network only.
- **Debug override.** A new `docker-compose.override.ports.yml` publishes the MCP servers, channel
  servers, agent, webui, observability, Camoufox and tse-extractor on `127.0.0.1`, on today's host
  port numbers, so `appsettings.Local.json`, `.vscode/launch.json` and `.zed/debug.json` keep
  working. The VS Code tasks that bring the stack up for debugging include it; the `launch-stack`
  skill documents it.
- **Outposts.** `assistants.*` gets a `handle /api/outposts*` route to the agent. The outpost docs
  and the `--hub` example become `https://assistants.<domain>`. The agent's dial-back to a machine
  is unchanged. The bridge (`/api/vfs-bridge`) stays unrouted.
- **Redis.** A `REDIS_PASSWORD` secret (placeholder in `.env`). The Redis container gets
  `--requirepass`, and its healthcheck authenticates. Each service's Redis connection string is
  given in its environment with the password. One password, not an ACL user per service.
- **Per-service environment.** No `env_file`. Each service's `environment` names the variables it
  reads, as `${VAR}`. `.env` stays tracked as placeholders. The OS overrides mount
  `usersecrets/<UserSecretsId>` per project instead of the whole directory. Agent and Observability
  share an id, and so share one mount.
- **Per-server MCP credentials.** Each server and channel server keeps reading
  `MCP__SHAREDSECRET`; the compose file feeds each one from its own variable
  (`MCP_SECRET_VAULT`, `MCP_SECRET_SANDBOX`, …; placeholders in `.env`). The agent gets
  `Mcp:Secrets`, a map from endpoint host to secret, and presents each configured endpoint and
  channel connection the one for its host. A host the deployment doesn't name is still dialled with
  no secret, as today. `Mcp:SharedSecret` goes away. A host in the agent's config with no secret
  fails at startup.
- **CORS.** The agent and the SignalR channel take `Cors:AllowedOrigins` from configuration, a
  per-deployment value in the compose environment. Only those origins get CORS headers, with
  credentials. Empty means same-origin only.
- **Hub registration.** Every hub method except `RegisterUser` refuses a connection that hasn't
  registered. Approval ids become a full GUID (128 bits) on SignalR and Telegram; Telegram's
  callback data (64 bytes at most) still fits the prefix and 32 hex characters.
- **Telegram ids.** `AllowedUsernames` becomes `AllowedUserIds`, numeric. A message is admitted only
  when `From.Id` is on the list; the `Chat.Username` and `Chat.FirstName` fallbacks go. The turn's
  sender stays the username, so memory and the identity map keep their keys until Accounts moves
  them to user ids. The bot records the numeric id of the person whose message started each turn;
  an approval tap counts only when `callbackQuery.From.Id` matches it, and anyone else gets "This
  approval isn't yours". An approval asked by a fire is answered by any allowlisted person in that
  chat.
- **No working defaults.** `Announce__Token` is empty in `.env`. The voice hub and the HA
  watch-fired endpoint already refuse every call while it is unset (`VoiceHubAuth.TokenMatches`),
  so this is only the placeholder. Every other secret placeholder is already empty.

## Testing Decisions

Test what a caller observes: what a request gets back, what a client is refused, what the compose
file declares. Never which internal method ran.

- **Compose conformance** (prior art: `VfsBridgeApiTests.ThePublicProxy_DoesNotRouteTheBridge`,
  which reads the Caddyfile). Reads `docker-compose.yml` and asserts: no `env_file`; no port
  published without a bind address except Caddy's 443; Redis bound to loopback; every MCP server
  fed its own secret variable, no two sharing one; the sandbox's environment names nothing but its
  own secret; no service except the ones that read them gets the OpenRouter key, the HA token or
  the logins key. The debug override publishes only on `127.0.0.1`. The Caddyfile routes
  `/api/outposts` and still not the bridge.
- **Per-endpoint secrets.** The agent spec projection hands each endpoint its own host's secret
  (prior art: the tests around `AgentSpecProjection`). A real server host refuses another server's
  secret (prior art: the `Mcp.Hosting` gate tests).
- **CORS and the hub.** A test host sends a preflight from an allowed and a foreign origin. Hub
  tests: an unregistered connection calling `GetHistory`, `DeleteTopic` or
  `RespondToApprovalAsync` is refused; approval ids are 32 hex characters.
- **Telegram.** `ProcessUpdateAsync` and `ApprovalCallbackRouter` with a fake bot client: an
  allowlisted id is admitted; a non-allowlisted id whose username or first name is on the old list
  is refused; another person's tap leaves the approval pending and is told it isn't theirs; the
  turn's person's tap resolves it.
- **Stacks that wire containers as compose does.** The E2E `WebChatStack`, the integration
  `JonasMcpStackFixture` and the eval's stack start their containers themselves rather than from
  the compose file. They give every server its own secret and the agent the matching `Mcp:Secrets`,
  and the existing suites pass through them unchanged. Their own Redis testcontainers may stay
  passwordless; one stack runs with a password so the connection-string wiring is exercised.

## Out of Scope

- A real WebChat user, per-user hub authorization, dashboard and Sugary sign-in: Accounts.
- Signed identity in `_meta`: Service identity. Here a server still believes any `_meta` from a
  caller holding its credential.
- An ACL user per service in Redis, TLS between containers, and encryption of Redis data.
- Lemonade's own auth: the speech typist reaches it on the LAN; whether the box checks a key is the
  box's configuration.

## Further Notes

Deploying needs the operator to add `LAN_BIND_ADDRESS`, `REDIS_PASSWORD`, every `MCP_SECRET_*` and
`CORS__ALLOWEDORIGINS` to prod's `.env`, set `Announce__Token` on both the voice hub and Home
Assistant's `rest_command`, re-point each outpost's `--hub` at Caddy, and read Redis through
`ssh -L 6379:127.0.0.1:6379 192.168.5.45` from then on.
