---
paths:
  - "Accounts/**"
  - "Tests/Integration/Accounts/**"
  - "Tests/Integration/Fixtures/AccountsServer.cs"
---

# The accounts service

See also: `channel-identity.md` (the internal API channel servers resolve people through), `tunnel-credentials.md` (households' servers' credentials), `custom-agents.md` (SexyTime's agent registration API).

`Accounts/` is the only owner of households (tenants), people (users), roles, passkeys, invites, recovery links and sessions. It is a plain ASP.NET Core host, **not an MCP server**: no `Mcp.Hosting`, no Infrastructure; its one project reference is Domain (signing: `.claude/rules/service-identity.md`; sealing and configuration shapes: `.claude/rules/household-config.md`), built `FROM base-sdk` with `BuildProjectReferences=false`. The container `accounts` is on the compose network only; Caddy's `handle /accounts/*` passes the path on unstripped, so every route is mapped under `AccountsApp.PathBase` (`/accounts`).

## The store

EF Core on SQLite in `./volumes/accounts`, migrated and seeded before the first request and before every command (`AccountsDatabase`). **Nothing in the model or the migrations names SQLite**, so a hosted database is a provider swap:

- Keys are opaque generated strings (`Tokens.NewId`), never a name and never a generated integer (no `Sqlite:Autoincrement`); key columns carry a max length. Instants are `DateTimeOffset` converted to UTC `DateTime` (`ConfigureConventions`), enums are strings, no raw SQL, no JSON columns.
- A migration is generated with `dotnet ef migrations add <Name> --project Accounts/Accounts.csproj --output-dir Store/Migrations`, then **stripped of the provider's store types** — every `type: "TEXT", ` in `Up`/`Down` and every `.HasColumnType(...)` in the designer and the snapshot. `dotnet ef migrations has-pending-model-changes` must still say nothing changed; `MigrationsMatchTheModelTests` catches a model change no migration carries.

Every record carries its tenant. A channel identity is a row (`LinkedIdentity`, unique on (`Channel`, `Identity`) and on (`UserId`, `Channel`); a group is a `BoundChat`, unique on (`Channel`, `ChatId`) so a chat is one household's; generic by channel), so `Users` never grows a column per channel and another chat transport is rows, not tables. `Identity` and `ChatId` are as the channel spells them — a Telegram id's (a group's negative) decimal digits — which the channel server must match. A voice profile needs no row: it is named after the user id.

## Secrets at rest

A link's token and a session cookie's value are bearer secrets: only their SHA-256 hash is stored (`Tokens.Hash`), so the database alone opens nothing. Device lists and revokes name a session's or passkey's opaque `Id`. Tunnel credentials likewise (`tunnel-credentials.md`).

## Links

- **A person's newest recovery link closes their earlier ones** (`LinkStore`'s one recovery mint, whoever mints): one sent to the wrong person is taken back by minting another (user's decision, 2026-11-05).
- An invite creates its person, with the name and role it names, in its minter's household on redemption (`LinkStore.MintInviteAsync`, `RedeemInviteAsync`); a recovery only adds a passkey to somebody who exists.
- A link's token (`Tokens.NewSecret`, 43 base64url characters) is also the bot's start parameter, so it must stay inside Telegram's 64 of `[A-Za-z0-9_-]` (`LinkStore.MintTelegramCodeAsync`).
- Only invite and recovery open the enrollment page (`LinkStore.Enrollable`), and each Telegram call finds a link by the kinds it redeems alone (start parameter: invite and `TelegramLink`; `/bind`: `TelegramGroup`), so a recovery link is no start parameter and an invite binds no group.
- A link is good once for `LinkLifetimeDays` (7). It is checked when its page opens and when its ceremony begins, but **spent** only at the ceremony's finish, by a conditional update inside the transaction that adds the passkey (`LinkStore.SpendAsync`), so two ceremonies racing on one link cannot both write.
- A redemption without a passkey keeps the same rule: `LinkStore.RedeemInviteAsync` spends the invite and creates the person **inside the caller's transaction**, and `TelegramLinks.RedeemStartAsync` spends an invite or link code and writes the sender's `LinkedIdentity` in one transaction, so a person never exists without the way in that made them. A Telegram another person holds — or anyone at all, for an invite — is refused (409) **before anything is spent**, so the code stays open for the Telegram it was meant for; the unique index decides a race the read missed.

## Sessions

- The cookie is `ziggurat_session` (`SessionCookie`): HttpOnly, Secure always (localhost counts as secure in a browser, so no development exception), SameSite=Lax, on `SessionCookie:Domain` (the parent domain; empty for host-only localhost), valid until revoked.
- A session or passkey is revoked **named by its owner as well as its id** (`RevokeAsync(userId, id)`); the ids are on the account page, so not secrets. `PasskeyStore.RevokeAsync` revokes the passkey and the sessions it began in one transaction (a `mint-session` session has no passkey and survives). Revoking a session or passkey, disabling a member and signing out announce `SessionsEnded` so the chat hub closes the connections those sessions held (`.claude/rules/chat-hub.md`).
- A person's **last passkey cannot be revoked from their account page** (`PasskeyStore.RevokeOwnAsync`, counted in the revocation's transaction, 409, drawn with no button): only an owner's recovery link would let them back in.

## The account page

- **An installed app has no Back button or address bar**, so every page of this service carries a `returnUrl` (chat client: `SignedInBadge` → `IAccountService.OpenAccount`; Sugary from its settings). Back (`data-back`) follows it only when `ReturnUrls.Allowed`, else goes to `/` of the page's host; every form action, 303, error page and stale-form page carries it on (`AccountPage.Here`).
- **Sign out** is `POST /accounts/sign-out` (antiforgery; never refused for a suspended household): revokes, announces `SessionsEnded`, `SessionCookie.Clear`, 303 to `sign-in?returnUrl=<back>&signedOut=1`.
- The avatar is empty, an `http(s)` address, or a path on the deployment's hosts starting with exactly one `/`, with no whitespace, control character or `\` (`Profile.Read`): every app draws it as an image, and a browser reads `//host` or `/\host` as somebody else's.
- The page names who holds "sees all conversations" and who reads everyone's Sugary log, in the app and through their agent (`data-reads-sugary-logs`) to everyone in the household, deliberately, and prints `scripts/enroll-voice.sh <user id>`: renaming that script means changing the page (`AccountPageTests`).
- Members are managed in the chat client's admin area (`.claude/rules/admin-owner-pages.md`, "Members"); `/accounts/members` redirects there (`MembersPage`). **No recovery link is ever minted for the operator** by anyone but the container command: an owner who could mint one could take the dashboard.

`/accounts/*` is **not** behind the proxy's session check (sign-in and enrollment open signed out) — except `/accounts/admin/*`, which is, and believes only that check's assertion (`.claude/rules/admin.md`). So `PageGuard` reads `ziggurat_session` itself (signed-out page → sign-in, `/accounts/api` → 401) and refuses a **suspended household**'s session first, before antiforgery or the owners check (`SessionCheck.Suspended`).

- Every POST carries an antiforgery token (`RequestVerificationToken` header for the script): every host under the parent domain is the same site, and these forms decide who gets in. The antiforgery cookie `ziggurat_antiforgery` is host-only on `/accounts` and **not** Secure — TLS ends at the proxy, and antiforgery throws rather than issue a Secure cookie on plain HTTP. Data-protection keys live in the volume (`keys/`).

## Disabled members

An owner disables a member through the admin API (`.claude/rules/admin-owner-pages.md`, "Members"): `User.DisabledAt` set and every session revoked in one transaction (`MemberStore.DisableAsync`). **Nothing else is deleted or spent** — every store that vouches for a person refuses a disabled one itself, so re-enabling is clearing the column:

- **Sessions** (`SessionStore`): none issued, and `VerifyAsync`/`LiveAsync` answer nobody, so the session check, `/accounts/me` and the chat hub's per-turn refresh refuse them even for a session that raced the revocation; a re-enabled member's old sessions stay revoked. Sign-in says "disabled" (`PasskeyEndpoints.DisabledAccount`, 403) only after the signature verified; anything else refused reads as an unknown passkey (`SignInOutcome`).
- **Links** (`LinkStore.FindOpenAsync`): a link naming a disabled person opens nothing (enrollment 410) until re-enabled. **What an owner handed out** — an invite, a group code, another person's recovery link — opens only while its minter (`Link.CreatedBy`) is still an enabled owner of the household, or the operator (user's decision, 2026-11-05). A link minted by nobody (the command) or by the person it names is left alone.
- **Channel identities**: `resolve` answers 404 on every channel (voice → guest, Telegram → stranger); `TelegramLinks.PersonAsync` and `TelegramIdOfAsync` find nobody.
- **The agent** is told through `AgentHouseholdConfig.DisabledPeople`, announced as `HouseholdConfigProtocol.PeopleChanged` on every change, and refuses their turns and their delegations' fires (`.claude/rules/person-assertions.md`, "Disabled members"); Telegram's cached resolution (`LinkCache:Linked`, 5 min) still forwards for a while, answered `ConversationGroup.DisabledAnswer`.
- **Recovery links** are minted for nobody disabled, whoever asks: `LinkStore.MintRecoveryAsync` answers a `RecoveryMint` (`Minted`, `Nobody`, `Disabled`) and every caller says why.
- **Dreaming** and extraction (`MemoryExtractionWorker`) skip them by the same `DisabledPeople`, though the `people` view still lists them (`.claude/rules/memory-architecture.md`); memories are kept.

## The session check

`GET /accounts/verify?channel=<id>`, for `forward_auth` alone (`accounts:8080` on the compose network); every host that routes `/accounts/*` answers `/accounts/verify*` 404 (`.claude/rules/admin.md`, "The proxy"), so no browser script reads an assertion.

- A valid session answers 200 with `X-Ziggurat-User`, `X-Ziggurat-Tenant`, `X-Ziggurat-Roles` (`owner`|`member`, plus `operator`, plus `sees-all`) and `X-Ziggurat-Assertion`, **always all four** (`ForwardedAccount.Headers`; every `copy_headers` copies all four, conformance-checked).
- The assertion signs those three plus `sid`, for the `channel` the block names — `signalr` (chat client), `sugary`, `dashboard` and `sexytime` (no turns), `admin` (this service's admin API) — because the agent refuses a turn whose assertion names another channel. `channel` is **required** (400 without), so a new `forward_auth` block cannot hand an app an assertion naming nothing.
- **Never add a site-level strip of `X-Ziggurat-*`**: Caddy runs it after `forward_auth` and blanks what `copy_headers` set.
- Sees-all rides as a role (`ForwardedAccount.SeesAll`, `SeesAllRole`); the hub reads roles once per connection, so a role or sees-all change is announced as `PeopleChanged`: the hub sends the household's connections `OnAccessChanged`, and the page renews its connection (`IChatLiveConnection.RenewAsync`, `AccessChangedEffect`) and reloads its list.
- `?role=<role>` answers a valid session lacking it 403, not a sign-in redirect.
- A **suspended household**'s session (`Tenant.SuspendedAt`, `.claude/rules/admin-operator.md`) is checked first and answered 403 on every channel and role, never the four headers: a page gets the "Household suspended" page (`SessionCheck.SuspendedNotice`), anything else the notice as plain text. The session is kept.
- With no session, a **page** (`X-Forwarded-Method` GET, first `X-Forwarded-Uri` segment not `api` or `hubs`, `Accept` naming `text/html`) gets a 302 to `{PublicBaseUrl}/accounts/sign-in?returnUrl=<absolute original>`; anything else 401. `ReturnUrls.Resolve` follows a return only onto `AllowedOrigins`.

## Who can forge the headers

Nobody, for the apps that act for a person: the chat hub, Sugary's API, the dashboard and the agent registration API believe only `X-Ziggurat-Assertion`, verified against this service's JWKS and held to their own channel, **never the plain three** (`.claude/rules/person-assertions.md`, "Apps behind the proxy"). The chat client's host (`webui`) serves files and reads no header; this service's own pages read their own cookie. The sandbox is kept off them by network (`.claude/rules/sandbox.md`, `TenantKitConformanceTests.TheSandbox_SharesANetworkWithNoOtherKitService`).

## SexyTime and the agent registration API

`sexy.*` carries the same `forward_auth` on its catch-all and routes `/accounts/*` unchecked, so a page there reads `/accounts/me` same-origin; `https://sexy.…` is one of `AllowedOrigins`. The agent registration API (`/api/agents*`) is `custom-agents.md`'s.

**An installed app's own files are served signed out** on the main host, Sugary's and SexyTime's (`@shell` in the Caddyfile, before the checked catch-all): a request that is not a page being opened (no `Sec-Fetch-Mode: navigate`, no `text/html` in `Accept`) and not under `/api/*` skips the session check, because a browser replaces a service worker by fetching its script and shell files signed in or not, and an old worker answers every navigation, sign-in's included, from cache: with those files checked such an installed app could never be replaced nor reach sign-in. Pages and APIs stay checked (`DeploymentConformanceTests.TheAppsOwnFiles_AreServedSignedOut_ButNoNavigationAndNoApi`, smoke `AnInstalledAppsFiles_AreServedSignedOut_ButNotItsPagesOrItsApi`). The dashboard's host does the same through `forward_auth @checked` (never `/api`, `/hubs` or `/accounts`), its own gate passing such a request only for a file of its web root (`OperatorGate`, `.claude/rules/observability.md`).

## The app sign-in

How a built app's origin, on the apps domain no cookie of this service reaches, learns who is viewing
(`.claude/rules/apps.md`, "App sign-in"; the wire in `Domain/Apps/AppSignIn`):

- **`GET /accounts/app-sign-in?returnUrl=&state=`** (`Apps/AppSignInRoute`), a page on the main host
  under the unchecked `/accounts/*`. A `returnUrl` that is not a built app's origin under `AppsDomain`
  (`AppSignIn.ReturnTo`: `AppOrigin.Parse` on the host, https, plain http only under `.localhost`, no
  userinfo; empty domain matches nothing), or a `state` not of its shape, is 400 before anything is read.
  Signed out: 302 to sign-in with this route as its return (state carried). A suspended household:
  `SessionCheck.Suspended`. Else 302 to `<origin>/_app/sign-in?code=&path=&state=`.
- **The apps domain is not `AllowedOrigins`**: those are Fido2's origins and sign-in's returns, held to
  the Caddyfile's hosts; `AppsDomain` (`APPSDOMAIN`, the `x-apps-domain` anchor the proxy and the apps
  server read) is a setting of its own.
- **A code** (`AppSignInCodes`, in memory, `Tokens.NewSecret`) stands for (origin, user, session), lives
  `AppSignInCodeLifetime` (1 min) and is **taken on the first redemption, whatever origin it names**.
- **`POST /accounts/internal/app-sign-in`** `{ code, origin }` (`Internal/AppSignInRedemption`) — only a
  caller that may ask about `AppChannels.Apps` (`InternalCallers.Apps`; anyone else 403, nothing spent) —
  answers `ResolvedIdentity` with an assertion on `apps` naming the session's `sid`, or 404 for a code
  unknown, spent, run out or issued for another origin, and for a session revoked or a household
  suspended since. Renewal is the session refresh on `apps` (`channel-identity.md`).
- What the person may open is the apps server's decision; this service vouches only for who they are.

## Who is signed in

`GET /accounts/me` answers a valid session with JSON `{ userId, tenantId, displayName, avatarUrl, roles }`, `no-store`, anything else 401 — never a redirect (`SignedInPerson`). It is **not** behind the session check; the chat client reaches it same-origin, and its 401 is what sends the page to sign in. An app on another host needs `/accounts/*` routed on its host, or asks across origins with credentials.

## Assertions

`Signing/Assertions` signs `IdentityClaims` as `TokenIssuers.Accounts` for `TokenAudiences.Assertion` — **one audience for every consumer**; the channel binds an assertion to a use — for `AssertionLifetime` (10 minutes), on `AccountsClock`. No assertion is stored, so losing the key costs only a moment.

- The key is `SigningKeyStore` over `Signing`: `signing/` beside the database unless `Signing:KeyDirectory` is set, generated on first use; `SIGNING__PRIVATEKEY` hands a test stack a fixed key nothing writes, rotates or retires. Nothing about it is in `.env`.
- `GET /accounts/internal/jwks` (`InternalApi.JwksPath`) is the one internal route **outside** the caller filter, kept off the internet only by the proxy's `/accounts/internal/*` 404 (`DeploymentConformanceTests.TheAccountsJwks_IsRefusedThroughTheProxyOnEveryHost`).

## Ceremonies

Fido2 with discoverable credentials, relying party `WebAuthn:RelyingPartyId` (the parent domain), origins `AllowedOrigins` (`PasskeyCeremonies`); options held server-side between the two requests (`CeremonyStore`, taken once). The pages (`AccountsPages.Page`, one script `PasskeyScript`) send a strict CSP and `no-referrer`, since the enrollment page's address carries its token; glyphs are inline SVG only. `/accounts/theme.js` reads the chat client's `localStorage` key `theme` (`WebChat.Client/wwwroot/app.js`): rename both or neither.

- Sign-in offers the passkey unprompted **only in an installed app** (`display-mode: standalone`) and **at most once a minute** (sessionStorage), never after `signedOut=1`; a refused or dismissed auto-start puts the button back without a word (`PasskeyScript`). Deliberate: do not make it offer everywhere or on every load.

## Commands

`docker exec accounts dotnet Accounts.dll <verb>` — the verbs are listed on `AccountsCommand`. `Program` runs a verb **instead of** the web server, its arguments kept out of configuration; every verb gets the input (`docker exec -i`). Scripts read their output, so keep it stable:

- `mint-link --operator [--name <display name>]` | `--user <id>`, `create-user --name <display name> [--role owner|member] [--sugary]`, `mint-session --user <id>` (called by `scripts/` and the test stacks). `mint-link` and `create-user` print `user <id>, household <id>`; `mint-session` prints only the cookie's value.
- `mint-tunnel-credential --household <id> --server <name>` prints `credential <id>` then, last and alone on its line, the credential, never shown again (exit 2 for an unreadable name, 1 for no such household); `list-tunnel-credentials` never prints a secret; `revoke-tunnel-credential --id <id>` exits 1 when the relay could not be told (`tunnel-credentials.md`).
- `rotate-signing-key` / `retire-signing-keys` touch no database and, with `SIGNING__PRIVATEKEY` set, change nothing and exit 1. Run `retire-signing-keys` only once the previous key's last assertion has run out — the lifetime plus 30 s of skew, which the rotation prints (`service-identity.md`, "Keys"); retiring at once unpublishes a key live assertions were signed with.
- `get-household-config`, `set-household-config`, `rewrap-household-keys` are `.claude/rules/household-config.md`'s.

## Configuration

- Per-deployment compose entries, not secrets: `WEBAUTHN__RELYINGPARTYID`, `ALLOWEDORIGINS__n`, `SESSIONCOOKIE__DOMAIN`, `PUBLICBASEURL`; `DeploymentConformanceTests` checks they describe hosts the Caddyfile serves, and missing ones refuse startup. `APPSDOMAIN` (optional: empty signs nobody in to an app) is held equal to the proxy's and the apps server's.
- `Telegram:BotUsername` (`TELEGRAM__BOTUSERNAME`) is optional: empty shows the bare code, which is safe; a wrong or placeholder name would hand every code to whoever owns that bot.
- Its one secret is `HOUSEHOLDCONFIG__MASTERKEY` (`.claude/rules/household-config.md`); its `CALLERS__<host>` are its callers' existing credentials (`channel-identity.md`; a host run takes `local-mcp-secret` from the launch configuration). `AGENTKEYSURL`, `AGENTURL`, `REDIS__CONNECTIONSTRING` (announcements only, nothing stored) are explained on `AccountsSettings`.
- Its time is `AccountsClock`, never the container's `TimeProvider`, so a test moves the service's time without moving Kestrel's.

## Tests

`AccountsServer` boots the shipped composition on a real localhost port with its own database and a `FakeTimeProvider`; `VerifyAssertionAsync` checks an assertion as a consumer will. The ceremony suite uses CDP's virtual authenticator, which belongs to the page, so a sign-in after an enrollment runs on the same page with cookies cleared; a browser signs in without a ceremony by a `mint-session` cookie. `AccountsServer.SignedIn(session)` posts forms with the page's antiforgery token.
