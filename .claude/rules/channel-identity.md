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
  - "Tests/Integration/Accounts/SuspensionCoverageTests.cs"
  - "Tests/Integration/Accounts/TelegramLinkingTests.cs"
  - "Tests/Integration/Accounts/TelegramGateTests.cs"
  - "Tests/Integration/Accounts/AccountsResolverClientTests.cs"
---

# The accounts service's internal API

See also: `accounts.md` (the service, sessions, links, assertions), `tunnel-credentials.md` (the relay's routes on this API), `custom-agents.md`.

## Resolving a channel identity

`POST /accounts/internal/resolve` (`Internal/IdentityResolution`) is how a channel server learns whose account the identity it saw is: `{ channel, identity }` answered `{ tenantId, userId, displayName, assertion }`, or 404 for an identity nobody holds. `voice` identities are the account's own id (a voice profile is named after its user id), `telegram` is a numeric id a `LinkedIdentity` row links, and any other channel is 400 — `signalr` included: the chat hub's person is a live session's, never an account named by its id, which would let the hub's credential mint anybody.

`POST /accounts/internal/session` (`Internal/SessionRefresh`) is the chat hub's, and Sugary's (asked before a pen reminder reaches a phone a session subscribed): `{ userId, sessionId }` — the session its connection's assertion named (`sid`) and its person — answered like a resolution, with a fresh assertion on the caller's own channel (`signalr` or `sugary`) naming that session, while the session is live, and 404 once it is revoked, gone, or not that person's (`SessionStore.LiveAsync`). The hub asks it for every turn (`ISessionAccounts`), so revoking a session stops its open chat client's next turn.

`POST /accounts/internal/guest` `{ channel }` answers `{ tenantId, assertion }`: a household's **guest**, signed (`IdentityClaims.Guest`), for a satellite whose speaker no profile matched — the home's for `mcp-channel-voice`, the credential's household for a household's own voice server (below). `voice` alone, any other channel 400, because only a satellite hears somebody nobody sent.

The Telegram bot's ways in sit under `/accounts/internal/telegram/` (`Internal/TelegramLinking` over `Links/TelegramLinks`), each answered by a status the bot tells its person apart by:

- `start` `{ telegramId, code }` → 200 `{ outcome: invited|linked, tenantId, userId, displayName }`, 404 for a code nothing open goes by, 409 for a taken Telegram.
- `web-link` `{ telegramId }` → 200 `{ url, expiresAt }` (a recovery link the person asked for themselves), 404 for a Telegram nobody linked, **403 for the operator**, whose Telegram must not be a way onto the dashboard (their way is the account page or the container command).
- `bind` `{ chatId, code }` → 200 `{ tenantId }`, 404, or 409 for a group another household holds (one bound to the same household is bound again, idempotently).
- `group` `{ chatId }` → 200 `{ tenantId }`, or 404 for a group bound to none.
- `of-account` `{ tenantId, userId }` → 200 `{ telegramId }`, the person's linked Telegram and so their private chat with the bot, where an approval their turn asked is offered; 404 for a person who linked none or a household that does not hold them.

The client is `ITelegramAccounts` (`Domain/Contracts`), answered by the same `AccountsResolverClient`, which `AddAccountResolver` registers as both contracts. These routes are **server to server only**:

- A caller presents `Authorization: Bearer <credential>`, matched against every `Callers` entry (`CALLERS__<host>`) with `Domain.Security.SharedSecret.Matches` (the comparison every shared-secret gate uses); anything else is 401 before a row is read. The check is one endpoint filter on the `/internal` route group (`Internal/InternalApi`), so an endpoint added there is covered without saying so.
- The credential is the one the caller already holds for its own `/mcp` — compose feeds `CALLERS__mcp-channel-signalr` the same `${MCP_SECRET_CHANNEL_SIGNALR}` that server's `MCP__SHAREDSECRET` reads, and likewise Telegram's and the shared servers' — so no secret exists for this alone; `DeploymentConformanceTests` holds each entry to its server's variable. An empty entry admits nobody, and a server that asks nothing here is given no entry. Sugary resolves no identity (it reads the person off the forwarded assertion); it asks only whether a session that subscribed a phone still lives, on `sugary`, and for its configuration views.
- **Each caller asks about its own channel alone** (`Internal/InternalCallers`, by the `CALLERS__<host>` name): `mcp-channel-signalr` refreshes sessions on `signalr`, `mcp-channel-telegram` resolves `telegram` and is the only caller of `/telegram/*`, `mcp-channel-voice` resolves `voice` and asks for the guest (the table keeps it, but the shipped compose gives it no entry: a household's voice server asks on its back-call, below), `mcp-sugary` asks after a session on `sugary`. Anything else is 403 before a row is read (a channel no endpoint serves is still 400 first), and a host named with no channel asks about nothing. So one leaked channel credential mints that channel's people and no other's.
- **A household's own voice server** is a caller of a third kind (`InternalCallers.HouseholdServer`), on its accounts back-call alone (`.claude/rules/tunnel.md`, "Back-calls"). Presenting no `CALLERS` entry, it is admitted by the **live tunnel credential** the relay shows on its behalf (`TunnelCredentialStore.LiveAsync`; revoked or unknown is 401), and only one minted for `HouseholdServerNames.Voice` — any other live credential (a printer's, a vault's, an outpost's) is 403, here as well as at the relay, because a resolution signs an assertion for anybody in the household named by id (`PresentingHouseholdServerAsync` answers `HouseholdServerPresented`). It may ask about `voice` alone (resolve and guest; `/tunnels/verify` is 403), and only about the credential's household: `resolve` answers 404 for a person of any other, and `guest` answers that household's guest instead of the home's. No header names it; the credential does.
- A credential several entries share — a host run's one local secret — is each of those callers at once.
- Compose's caller names are held to the table (`DeploymentConformanceTests.EveryChannelCallerOfTheAccountsService_IsOneItKnowsTheChannelOf`). The same table says which view of a household's configuration each caller may read under `/accounts/internal/household-config/` and for which household its forwarded call token or assertion proves (`.claude/rules/household-config.md`).
- Every Caddy host that reaches this service answers `handle /accounts/internal/*` with `respond 404` **before** `/accounts/*` (conformance-checked), so a browser never reaches it; a new host routing `/accounts/*` needs the same block, and the session check's `/accounts/verify*` one beside it.

`POST /accounts/internal/household-import` is the exception to "any listed caller": it can make the operator and mint their passkey link, so it answers only `CALLERS__household-migration` (403 for any other caller), a credential the deployment leaves empty but for the run (`.claude/rules/household-migration.md`).

Every identity the internal API resolves is answered with an `assertion` of that person on the channel asked about: `resolve` (`ResolvedIdentity.Assertion`) and the Telegram `start` (`TelegramStarted.Assertion`, on `telegram`); `bind`, `group` and `of-account` resolve no person and sign nothing.

**A suspended household's people are neither somebody nor nobody**: `resolve` for one, the guest of one, a Telegram `start` with one's code (nothing spent, so it opens once resumed) and `web-link` for one answer `SuspendedHouseholdAnswer.Status` (423), never an assertion, so no channel vouches for them. The client throws `HouseholdSuspendedException` for a resolution or a guest and answers `TelegramStartOutcome.Suspended` / `TelegramWebLinkOutcome.Suspended`; the bot tells the sender (or a tapper) `TelegramGate.SuspendedAnswer`, keeping nothing, while voice falls to its own household's guest. The chat hub's `session` refresh still answers for one, deliberately: the agent then refuses the turn with `ConversationGroup.SuspendedAnswer`, where a refusal here would end the stream "signed out". `SuspensionCoverageTests` lists every route's stance and fails on a new route until it is placed.

The client every channel server shares is `IAccountResolver` (`Domain/Contracts`), implemented by `AccountsResolverClient` (`Infrastructure/Clients/Accounts`, registered by `AddAccountResolver(config, credential)` with the server's own `/mcp` credential, `Accounts:BaseUrl` defaulting to `http://accounts:8080`): null is nobody, and a call the service could not answer throws, so a caller never takes an outage for a stranger. Every person it answers (`ResolvedAccount`, the Telegram start's included) carries the service's `Assertion`, and `GuestAsync(channel)` answers the household guest (`ResolvedGuest`: tenant and assertion). A channel server carries the assertion on the turn it starts, never keeps it past half its life, and never reads it.

## Tests

The Telegram endpoints are driven raw (`TelegramLinkingTests`) and through the client (`AccountsResolverClientTests`), and the bot itself against a running service by handing `TelegramPollingHarness` a real `AccountsResolverClient` (`TelegramGateTests`); the bot's own rules run against `FakeTelegramAccounts`.
