# Multi-tenant, multi-user, secured: plan

Status: needs-triage

The umbrella plan for turning one household's deployment into a deployment that serves several
households, each with several people who sign in for real. Each phase below gets its own grilled
spec under `.scratch/<phase-slug>/` before any code; this file holds the decisions they share.

## Where we start (survey, 2026-10-29)

- **No user is authenticated anywhere.** WebChat's `ChatHub.RegisterUser` takes any string, and
  `/api/config` publishes the valid ones; that string reaches `Principal`, the identity map and a
  person's remembered web logins. Telegram matches `From.Username ?? Chat.Username ?? Chat.FirstName`.
  Voice is a satellite's fixed `"household"` unless a speaker is enrolled. ServiceBus takes the
  sender from the message body.
- **Hub methods and approvals check nothing.** History, topics, delete, cancel and approval answers
  work from any connection; approval ids are 8 hex characters; CORS allows any origin with
  credentials. Telegram approval taps never look at who tapped.
- **The perimeter is "VPN only".** Redis (no password), the hub, agent, dashboard, camoufox and every
  MCP port are published on `0.0.0.0`, bypassing Caddy. Seventeen services get the whole `.env`;
  the whole user-secrets directory is mounted into fourteen containers; `DockerCompose/.env` is
  tracked and ships `Announce__Token=announce_secret`.
- **One MCP secret opens all fourteen servers**, and its holder can claim any user in `_meta`. The
  sandbox, which runs model-written code, holds it.
- **Nothing is keyed by tenant, and almost nothing by an authenticated user.** Memory is keyed by the
  raw per-channel sender (one person, separate memories per channel). Vault, sandbox home, Sugary
  (no owner column), schedules, HA, outposts, spaces, topics, push subscriptions and the dashboard
  are deployment-global. Custom agents and approvals live in memory only.

## Decisions

| Topic | Decision |
|---|---|
| Tenant | A household. One deployment hosts several; each has several users. |
| Isolation | Logical, one stack. Every store keyed by an authenticated tenant id. |
| Web auth | Passkeys (WebAuthn) in our own user store. Invite links enroll a device. No external IdP, no self-signup. |
| Auth shape | A new **identity service** container owns tenants, users, passkeys, invites and sessions, and sets a session cookie on the parent domain. Caddy `forward_auth` checks every web request and passes the verified user and tenant on. Nothing user-facing is reachable except through Caddy. |
| Admin | The operator creates tenants and invites each owner; an owner invites members, sets keys and turns features on, in an admin area of WebChat. |
| Keys | A tenant brings its own: OpenRouter key, Home Assistant URL **and** token, and so on, stored encrypted. |
| Local models | Embeddings and speech-to-text stay on the operator's Lemonade for every tenant, usage tracked per tenant. Chat models and Jev go on the tenant's OpenRouter key. |
| Telegram | One shared bot. A user links their account from a one-time deep link (`t.me/<bot>?start=<code>`); the bot records the numeric Telegram id. A group chat is bound to one tenant. |
| Home-only features | Vault, library and downloads, printer, sandbox home, outposts, voice satellites and timers stay the operator's household's. Generalizing them is later work; the likely shape is a per-tenant mountable blob store, so new storage code takes a tenant even where only one exists. |
| Per-tenant config | Fully editable agents: an owner can edit prompts, skills, tool sets and models. jonas, nabu and sugary ship as templates; the eval guards the templates, not a tenant's edits. |
| Config delivery | A server fetches **its own section** of the call's tenant config from the identity service with its own service credential, cached briefly. Secrets never travel in `_meta`. |
| Encryption at rest | Secrets only for now, with a per-tenant data key wrapped by a master key. Memories, Sugary and conversations stay plaintext. Moving them under the same key later is expected, so the sealing goes through one seam. |
| Service trust | The agent signs a short-lived identity token (tenant, user, principal) per call with a key only it holds; servers verify with the public key. Every server gets its own credential; the sandbox gets none. A channel server's claim about a user is vouched for by the identity service, not asserted. |
| Within a tenant | Conversations are private to their person. Spaces stay shared rooms of the household. A tenant owner can list and stop anyone's schedules; a member sees their own. Sugary is per person. |
| Migration | One shot: the current household becomes the first tenant, the operator its owner, the identity map's people its users. Each person's memories from Telegram, WebChat and voice merge into one. Old schedules are stamped to their creator, or to the owner where nobody can be told. |
| Ids | Opaque generated ids for tenants and users. A name is never a key (a `:` in a userId already breaks memory key parsing). |
| Ambient context | None. Tenant and user are explicit fields on `ConversationContext`, `Principal`, `_meta` and per-call views such as `ScheduleFileSystem.For(caller)`; AsyncLocal is not an option. |

## Branching

Every phase branches from `web-identity` and merges back into it; master gets all of it, with
web-identity, in one merge. Master stays free for hotfixes, which are merged into `web-identity` as
they land. Since nothing ships before everything does, a phase closes no gap with an interim
measure that a later phase replaces.

## Phases

Order is by dependency. The numbers from the planning conversation shifted: service identity now
comes before tenants, because tenant ids on calls mean nothing until the call's identity is signed.

### 1. Security baseline (`.scratch/security-baseline/`)

Tenancy-independent hardening.

- Publish only what must be reached from outside: Caddy's 443, and the LAN ports with a real
  consumer (HA's UI, Lemonade for the speech typist, qBittorrent's peer port, others found in the
  spec). Everything else on the compose network only.
- Redis password, with an ACL user per service where it's cheap.
- Per-service environment: no `env_file` of the whole `.env`; each service gets the secrets it
  reads. `.env` leaves git for an `.env.example`; per-project user-secrets mounts instead of the
  whole directory.
- CORS: an allowlist of the deployment's own origins.
- Hub methods refuse an unregistered connection; approval ids get 128 bits. Acting only on the
  connection's own topics and approvals waits for Accounts, since the user is still client-asserted
  here.
- Telegram: numeric ids instead of usernames, and an approval tap is honored only from the user the
  approval was asked of.
- Per-service MCP credentials replace the one shared secret; the sandbox holds none.
- No shipped default for the announce token or any other secret.

### 2. Accounts (`.scratch/accounts/`)

Real users, with the tenant already in the model (only the home tenant exists yet).

- The identity service: tenants, users, roles (operator, owner, member), passkey enrollment from
  invite links, sessions, recovery by a fresh invite from an owner. Caddy `forward_auth` in front of
  WebChat and its hub, Sugary and the dashboard. `/browse/*` and the logins page keep their own
  link-claimed auth: the link is sent into the chat, and a Telegram-only or voice user opens it with
  no passkey session. The claim may be bound to the linked user.
- WebChat drops the user picker and the client-asserted `RegisterUser`; the hub takes its user from
  the verified session and acts only on that user's topics and approvals. `signalr` becomes a
  verified channel, which turns voice login back on.
- Telegram linking by deep link; voice speakers enrolled to users; unrecognized voice stays a
  household guest with no person's access.
- `IdentityMap`, `Identity:People` and `Identity:VerifiedChannels` retire into the store: a channel
  identity resolves to a user id, and `Principal` carries tenant and user ids.
- Memory, push subscriptions, web logins and approvals keyed by user id; Sugary gets an owner column
  and a per-person log; the dashboard shows the operator everything and an owner their tenant.
- The migration of today's data into the home tenant.

### 3. Service identity (`.scratch/service-identity/`)

- The agent signs a short-lived identity token per call; servers verify it and ignore an unsigned
  `_meta` identity. Fires and wakes carry their stamped principal into the same token.
- Channel servers obtain a vouched assertion for the user they authenticated (hub: the session;
  Telegram: the linked numeric id; voice: the enrolled speaker) instead of naming one.
- Key rotation and token lifetime.

### 4. Tenants (`.scratch/tenants/`)

- Every store keyed by tenant id: conversations, topics and spaces, memory, schedules, watches,
  push, metrics, Sugary, custom agents (made durable).
- Per-tenant config in the identity service: agents (prompts, skills, tools, models), features on
  or off, keys (OpenRouter, HA URL and token, Idealista), spaces. Secrets sealed under the tenant's
  data key.
- `MultiAgentFactory` and `McpClientManager` build per tenant: the tenant's OpenRouter key, its
  enabled servers, its agent definitions. This is the deepest change in the plan.
- Servers fetch their own section of tenant config per call; the HA server talks to the tenant's
  HA. Home-only servers refuse every tenant but the operator's.
- Per-tenant tokens for inbound callbacks (HA watch-fired, announcements); Lemonade usage tracked
  per tenant.
- The owner's admin area in WebChat: members and invites, keys, features, agents.

## Open, to grill per phase

- **Baseline:** settled; spec and tickets at `.scratch/security-baseline/`. LAN tools bind to a
  named interface, debug ports move to a loopback override, prod Redis is read over `ssh -L`,
  outposts register through Caddy, and the sandbox keeps a credential that opens only itself.
- **Accounts:** settled; spec and 14 tickets at `.scratch/accounts/`. The identity service is the
  `accounts` container on SQLite + EF (kept provider-neutral). Invites redeem by passkey or
  Telegram. Topics are private, shared per topic, and an owner-granted permission sees all,
  Telegram and voice review copies included. Sessions last until revoked, approvals belong to the
  asker on any of their channels, Sugary logs are per person with a shared pantry, and the
  ServiceBus channel is removed.
- **Service identity:** implemented 2026-10-30 on branch `service-identity` (9 tickets plus review fixes; full suite and smoke tier green; merged into `web-identity` 2026-10-31). The accounts
  service signs assertions, the agent signs per-call tokens and delegations for deferred turns,
  servers refuse unsigned calls, apps verify the assertion, keys are generated and published as
  JWKS, and a long turn keeps fresh call tokens while its identity is verified once at its start.
- **Tenants:** grilled 2026-10-31 (decisions below); split into three phases, each its own spec
  under `.scratch/tenants-<a|b|c>/`, each branching from and merging into `web-identity`.

## Tenants decisions (grilled 2026-10-31)

- **Split.** 4a *Tunnel*: the relay, tunnelled servers, outposts moved onto it, the home kit
  running over it, offline servers' tools omitted. 4b *Tenant config*: sealed keys, per-section
  JSON, prompts and skills as documents, per-tenant agent building and keys, tenant-keyed stores and
  the migration, timers on channels, spaces. 4c *Admin and onboarding*: the owner's admin area, the
  agent editor, probes, the operator's tenant list and suspend, usage.
- **Two kinds of server.** *Shared deployment servers* serve every tenant: scheduling, timers,
  Sugary, Idealista, WebChat (SignalR) and Telegram. *A tenant's own servers* run at the tenant's
  site and reach the stack only through the tunnel: web search (+ Camoufox, so it browses from the
  tenant's IP), vault, library, sandbox, printer, Home Assistant, voice. The home tenant is the
  first: it runs a **tenant compose kit** the repo ships (same images, env config, one tunnel
  credential per server) beside the deployment on the same machine.
- **The tunnel.** True tunnel, built into `Mcp.Hosting`: a server told "connect to <hub> with
  <credential>" opens one outbound WebSocket through Caddy and serves its MCP session,
  notifications (an HA watch's fire, a voice turn) and HTTP pages (web search's `/browse/*`, which
  Caddy routes to the relay) over it. One tunnel per server; the relay lives in the agent. The agent
  never dials a tenant-supplied address. **Outposts move onto it from the start** (the outpost
  binary uses the same code; no more dial-back). The tunnel credential proves "tenant X's server
  Y"; call tokens inside still say who each call acts for. The agent's JWKS reaches tunnelled
  servers **over the tunnel** (on open and on rotation), never a public route.
- **Offline servers.** A turn is built with the servers connected when it starts; an offline
  server's tools and skills are left out and one line of context names the offline servers. A call
  in flight when the tunnel drops fails with a clear error.
- **Config.** In the accounts service: one JSON document per tenant per section (agents, features,
  keys, spaces, servers…) with a `schemaVersion`, migrated forward by accounts on read and on save.
  Shared servers fetch their own section per call (tenant from the verified call token) from the
  accounts internal API, cached briefly and invalidated by a Redis message on save; secrets are
  unsealed by accounts and sent only to the server they belong to. A tenant's own servers are
  configured at its site.
- **Secrets.** A random data key per tenant seals its secrets (AES-GCM); the data key is wrapped by
  one deployment master key, the one new `.env` secret. Losing the master loses every tenant
  secret (owners re-enter them).
- **Model costs.** A tenant brings its own OpenRouter key before its agents run; every model call
  made for it (turns, workers, memory extraction, dreaming, Jev, image reads) is billed to it, with
  no fallback. The home tenant uses the deployment key. Lemonade is shared and metered per tenant.
- **Agents.** Prompt sections and skills become text documents shipped in the repo as defaults;
  ceilings, the skill index and snapshots run over them. Shipped templates are a barebones Jonas
  (owner names it) and Sugary only; jack, jonas, nabu and sugary become the home tenant's own agents,
  seeded from today's definitions. A tenant forks templates and edits prompt sections, skills,
  tools/servers and model in a plain editor with validation, a short revision history, reset to
  shipped and a diff when the shipped template changed. No eval on tenant edits this phase.
- **Telegram.** One shared deployment bot serves every tenant (a linked person's messages go to
  their tenant's default agent; `/agent` switches); the home tenant keeps its jack and jonas bots as
  tenant-owned bots in its config.
- **Stores.** Per-person stores stay keyed by user id (memory, push, web logins). Everything a
  household shares or the deployment counts gets the tenant id: topics and spaces, schedules,
  watches, custom agents (made durable), outposts, metrics. Existing data migrates into the home
  tenant.
- **Timers** become a shared server like scheduling: a timer rings on the tenant's satellites
  through its voice server when it has one, otherwise as an insistent alert on the person's
  channels (WebChat push, Telegram) with dismiss from there.
- **Spaces** are a tenant config section edited by the owner; a new tenant starts with one default
  space; the home tenant's current spaces migrate into its config; space keys carry the tenant id.
- **Callbacks.** HA is entirely in the tenant's network: its watch-fired callback reaches the
  tenant's HA server locally, which sends the fire to the agent over its tunnel. No public callback
  route.
- **Validation.** Saving a key probes it (OpenRouter, Idealista, …): a definite auth failure
  refuses the save, an unreachable provider saves with a warning; auth failures in use re-probe and
  show the owner. Own servers show connected, last seen and tools offered.
- **Onboarding.** The operator creates a tenant (dashboard or CLI) and mints the owner's link; the
  owner's first sign-in lands in the admin area (OpenRouter key, name the default agent, invite
  members); until a key is set the tenant's agents refuse turns with a clear message.
- **Admin area.** Owner-only in WebChat: members and invites, keys, features, agents (editor),
  spaces, own servers (create, credential shown once, connected/tools, revoke) and a usage view
  (tokens, cost, Lemonade seconds). The operator's dashboard gains a tenant dimension and a tenant
  list (create, suspend).
- **Removal.** Suspend only (sessions refused, turns refused, tunnels closed); deleting and
  exporting a tenant's data is a later phase.
- **Out of scope.** A hosted per-tenant blob store (own servers cover it; every VFS mount must still
  resolve per tenant); eval on tenant edits.
