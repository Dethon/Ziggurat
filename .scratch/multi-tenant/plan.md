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
- **Service identity:** token format and lifetime; how a worker's or a fire's token is minted; the
  public `/browse/*` routes on a listener of their own; the signing key the eval stack and the
  test harnesses (`WebIdentityServerHarness`) need.
- **Tenants:** the config schema and its versioning; the capability catalogue (which servers are
  cloud-safe); the agent editor (prompts and skills in a browser); validating a tenant's keys; the
  storage seam for a future per-tenant blob store; whether a tenant may run the eval on its edits.
