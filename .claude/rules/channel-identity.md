---
paths:
  - "Accounts/Internal/**"
  - "Accounts/Links/TelegramLinks.cs"
  - "Infrastructure/Clients/Accounts/AccountsResolverClient.cs"
  - "Infrastructure/Clients/Accounts/AccountsClientConfig.cs"
  - "Domain/Contracts/IAccountResolver.cs"
  - "Domain/Contracts/ITelegramAccounts.cs"
  - "Domain/DTOs/Channel/TelegramLinking.cs"
  - "Domain/DTOs/Channel/SuspendedHouseholdAnswer.cs"
  - "Domain/Exceptions/HouseholdSuspendedException.cs"
  - "Tests/Integration/Accounts/IdentityResolutionTests.cs"
  - "Tests/Integration/Accounts/InternalCallersTests.cs"
  - "Tests/Integration/Accounts/SessionRefreshTests.cs"
  - "Tests/Integration/Accounts/AppSignInRouteTests.cs"
  - "Tests/Integration/Accounts/SuspensionCoverageTests.cs"
  - "Tests/Integration/Accounts/TelegramLinkingTests.cs"
  - "Tests/Integration/Accounts/TelegramGateTests.cs"
  - "Tests/Integration/Accounts/AccountsResolverClientTests.cs"
---

# The accounts service's internal API

See also: `accounts.md` (the service, sessions, links, assertions), `tunnel-credentials.md` (the relay's routes on this API), `custom-agents.md`.

## Resolving a channel identity

The service and its clients (`IAccountResolver`, `ITelegramAccounts`, both `AccountsResolverClient`,
registered by `AddAccountResolver`) spell the wire separately — the service references no client
project — so a route's shape and status codes are changed on both sides together.

- **`resolve`** (`Internal/IdentityResolution`): `{ channel, identity }` → `{ tenantId, userId,
  displayName, assertion }`, 404 for nobody. Only `voice` (the account's id) and `telegram` (a
  `LinkedIdentity` row); any other channel is 400 — `signalr` included: the chat hub's person is a live
  session's, never an account named by id, which would let the hub's credential mint anybody.
- **`session`** (`Internal/SessionRefresh`): the chat hub's (Sugary's, before a pen reminder; the apps
  server's, renewing an app session) `{ userId, sessionId, channel? }` — the `sid` its connection's
  assertion named, with its person — → a fresh assertion naming that session while it is live, 404 once
  revoked, gone or not that person's (`SessionStore.LiveAsync`); never an account by its id alone. The
  channel is the one named, which the caller must be allowed (else 403), or with none the first of
  `signalr`, `sugary`, `apps` it is; the apps server always names `apps`, since a host run's one shared
  credential is the hub's too. The hub also names `persons-apps` (`AppChannels.PersonsApps`, its alone),
  for the apps server's listing of a person's apps behind WebChat's space switcher (`PersonsAppsClient`). The hub asks for every turn (`ISessionAccounts`), so revoking a session
  stops its open chat client's next turn.
- **`app-sign-in`** `{ code, origin }`: the apps server's redemption of an app sign-in code
  (`IAppSignIns`, `accounts.md`, "The app sign-in").
- **`guest`** `{ channel }` → `{ tenantId, assertion }` (`IdentityClaims.Guest`), `voice` alone (400
  otherwise): only a satellite hears somebody nobody sent.
- **Telegram's ways in** (`/accounts/internal/telegram/`, `Internal/TelegramLinking` over
  `Links/TelegramLinks`), each answered by a status the bot tells its person apart by:
  - `start` `{ telegramId, code }` → 200 `{ outcome: invited|linked, tenantId, userId, displayName }`,
    404 for a code nothing open goes by, 409 for a taken Telegram.
  - `web-link` `{ telegramId }` → 200 `{ url, expiresAt }`, 404 for a Telegram nobody linked, **403 for
    the operator**, whose Telegram must not be a way onto the dashboard.
  - `bind` `{ chatId, code }` → 200 `{ tenantId }`, 404, or 409 for a group another household holds
    (rebinding to the same household is idempotent).
  - `group` `{ chatId }` → 200 `{ tenantId }`, or 404 for a group bound to none.
  - `of-account` `{ tenantId, userId }` → 200 `{ telegramId }` (the person's private chat, where their
    approvals are offered); 404 for a person who linked none or a household that does not hold them.
- Every identity resolved carries an `assertion` on the channel asked about (`ResolvedIdentity.Assertion`,
  `TelegramStarted.Assertion`); `bind`, `group` and `of-account` sign nothing. A channel server carries
  the assertion on the turn it starts, never keeps it past half its life, and never reads it.
- **Null is nobody; an outage throws**, so a caller never takes an outage for a stranger.

## Who may call

These routes are **server to server only**:

- `Authorization: Bearer <credential>` is matched against every `CALLERS__<host>` entry with
  `Domain.Security.SharedSecret.Matches`; anything else is 401 before a row is read. One endpoint filter
  on the `/internal` group (`Internal/InternalApi`) covers every endpoint added there.
- The credential is the one the caller already holds for its own `/mcp` (compose feeds
  `CALLERS__mcp-channel-signalr` the same `${MCP_SECRET_CHANNEL_SIGNALR}`), held so by
  `DeploymentConformanceTests`. An empty entry admits nobody; a server that asks nothing gets no entry.
- **Each caller asks about its own channel alone** (`Internal/InternalCallers`): signalr sessions,
  Telegram (`telegram`, the only caller of `/telegram/*`), voice (resolve and guest — the table keeps
  `mcp-channel-voice`, but the shipped compose gives it no entry: a household's voice server asks on its
  back-call, below), Sugary (sessions on `sugary`; it resolves no identity), the apps server (`apps`:
  app sign-in codes and sessions). Anything else is 403 before a row is read, so one leaked channel
  credential mints that channel's people and no other's. A credential several entries share is each of those callers at once.
  Compose's caller names are held to the table
  (`DeploymentConformanceTests.EveryChannelCallerOfTheAccountsService_IsOneItKnowsTheChannelOf`); the
  same table gates household-config views (`.claude/rules/household-config.md`).
- **A household's own voice server** (`InternalCallers.HouseholdServer`, on its accounts back-call,
  `.claude/rules/tunnel.md`, "Back-calls") is admitted by the **live tunnel credential** the relay shows
  (`TunnelCredentialStore.LiveAsync`), only one minted for `HouseholdServerNames.Voice` — a printer's,
  vault's or outpost's is 403, here as well as at the relay, because a resolution signs an assertion for
  anybody in the household. It asks about `voice` alone and its own household alone (`resolve` 404 for
  others, `guest` its household's).
- **A household's own web search** (`InternalCallers.HouseholdWebSearch`) is admitted the same way, by a
  live credential minted for `HouseholdServerNames.WebSearch`. It is no channel and mints nobody: its one
  question is the `brave-search` view of its own household (`.claude/rules/household-config.md`).
- Every Caddy host reaching the service answers `handle /accounts/internal/*` with `respond 404`
  **before** `/accounts/*` (conformance-checked); a new host routing `/accounts/*` needs the same block,
  and the `/accounts/verify*` one beside it.
- `POST /accounts/internal/household-import` answers only `CALLERS__household-migration` (403
  otherwise): it can make the operator and mint their passkey link, so the deployment leaves that
  credential empty but for the run (`.claude/rules/household-migration.md`).

## Suspended households

**A suspended household's people are neither somebody nor nobody**: `resolve`, `guest`, Telegram `start`
(nothing spent) and `web-link` answer `SuspendedHouseholdAnswer.Status` (423), never an assertion. The
client throws `HouseholdSuspendedException` (resolution, guest) or answers
`TelegramStartOutcome.Suspended` / `TelegramWebLinkOutcome.Suspended`; the bot answers
`TelegramGate.SuspendedAnswer` and keeps nothing, while voice falls back to its own household's guest
(`McpChannelVoice/Services/TranscriptDispatcher.SpeakerOfAsync`). The hub's `session` refresh still answers for one, deliberately: the
agent then refuses the turn with `ConversationGroup.SuspendedAnswer`, where a refusal here would end the
stream "signed out". `SuspensionCoverageTests` fails on a new route until its stance is placed.

## Tests

`TelegramLinkingTests` (raw), `AccountsResolverClientTests` (client), `TelegramGateTests` (the bot
against a running service via `TelegramPollingHarness`); the bot's own rules use `FakeTelegramAccounts`.
