---
paths:
  - "Accounts/**"
  - "Tests/Integration/Accounts/**"
  - "Tests/Integration/Fixtures/AccountsServer.cs"
---

# The accounts service

See also: `channel-identity.md` (the internal API channel servers resolve people through), `tunnel-credentials.md` (households' servers' credentials), `custom-agents.md` (SexyTime's agent registration API).

`Accounts/` is the only owner of households (tenants), people (users), roles, passkeys, invites, recovery links and sessions (`.scratch/accounts/spec.md`). It is a plain ASP.NET Core host, **not an MCP server**: no `Mcp.Hosting`, no Infrastructure. Its one project reference is Domain — the signing piece (`Domain/Security/Signing`, `.claude/rules/service-identity.md`), the sealing piece households' secrets are sealed with and the configuration's shapes (`.claude/rules/household-config.md`) — built `FROM base-sdk` with `BuildProjectReferences=false`, since base-sdk already built Domain. The container is `accounts`, on the compose network only; Caddy's `handle /accounts/*` passes the path on unstripped, so every route is mapped under `AccountsApp.PathBase` (`/accounts`).

## The store

EF Core on SQLite in `./volumes/accounts` (owned by PUID/PGID through the `accounts-volume` one-shot, as Sugary's is). A hosted service migrates it and seeds the home household before the first request, and so does every command before it runs — the operator's first command may come before the service ever started. It migrates only when `GetPendingMigrationsAsync` says one is pending, as Sugary's store does: EF's migrator diffs the design-time model on every call. A model change no migration carries is `MigrationsMatchTheModelTests`' to catch. **Nothing in the model or the migrations names SQLite**, so a hosted database is a provider swap:

- Keys are opaque generated strings (`Tokens.NewId`, 32 lowercase hex: no `:` to break a composite key elsewhere), never a name and never a generated integer, so no `Sqlite:Autoincrement`. Key columns carry a max length, which an indexable key needs on other providers.
- Instants are `DateTimeOffset` converted to UTC `DateTime` (`ConfigureConventions`), enums are strings, no raw SQL, no JSON columns.
- A migration is generated with `dotnet ef migrations add <Name> --project Accounts/Accounts.csproj --output-dir Store/Migrations`, then **stripped of the provider's store types** — every `type: "TEXT", ` in `Up`/`Down` and every `.HasColumnType(...)` in the designer and the snapshot — so each provider maps a CLR type to its own default. `dotnet ef migrations has-pending-model-changes` must still say nothing changed, and `dotnet ef migrations script` emits the same SQL either way.

Every record carries its tenant. A user's channel identities are rows of their own, so `Users` never grows a column per channel: `LinkedIdentity` (`Channel`, `Identity` as the channel spells it — a Telegram id's decimal digits), unique on (`Channel`, `Identity`) so an identity is one person's, and on (`UserId`, `Channel`) so a person holds one per channel. A voice profile needs none: it is named after the user id. A group chat bound to a household is a `BoundChat` (`Channel`, `ChatId`, `TenantId`, the owner whose code bound it), unique on (`Channel`, `ChatId`). Both are generic by channel, so another chat transport is rows, not tables.

## Secrets at rest

A link's token and a session cookie's value are bearer secrets: only their SHA-256 hash is stored (`Tokens.Hash`), so the database alone opens nothing. A session and a passkey also have an opaque `Id`, which is what a device list or a revoke names. A tunnel credential is kept the same way (`tunnel-credentials.md`).

## Links

- `LinkKind.Recovery` adds a passkey to a person who already exists. **A person's newest recovery link closes their earlier ones** (their `ExpiresAt` set to now, by `LinkStore`'s one recovery mint, whoever mints): one sent to the wrong person is taken back by minting another (user's decision, 2026-11-05). The operator's first enrollment is a recovery of an account with no passkey yet.
- `LinkKind.Invite` names a display name and role and creates the person on redemption, in the inviter's household.
- `LinkKind.TelegramLink` is the account page's code that links a person's Telegram, redeemed as the bot's start parameter (a 43-character base64url token, inside Telegram's 64 of `[A-Za-z0-9_-]`); `LinkKind.TelegramGroup` is an owner's code that binds a group, sent to the bot there as `/bind <code>`. Neither opens the enrollment page — `LinkStore.Enrollable` is invite and recovery only — and each Telegram call finds a link by the kinds it redeems alone (the start parameter: invite and link code; `/bind`: the group code), so a recovery link is no start parameter and an invite binds no group.

A link's page opened where somebody else is signed in says whose the device is and whose it will be (`AccountsPages`, the enrollment GET). A link is good once for `LinkLifetimeDays` (7). It is checked when its page opens and when its ceremony begins, but **spent** only at the ceremony's finish, by a conditional update inside the transaction that adds the passkey (`LinkStore.SpendAsync`), so two ceremonies racing on one link cannot both write.

The authenticator stores the user handle at the ceremony's start, so an invite's user id is chosen then (`Tokens.NewId()`, held with the ceremony) and the person is created with it. `LinkStore.RedeemInviteAsync(db, linkId, userId)` spends the invite and creates the person **inside the caller's transaction**, the seam a redemption without a passkey uses: `TelegramLinks.RedeemStartAsync` spends an invite (creating its person) or a link code (`SpendAsync`) and writes the sender's `LinkedIdentity` in one transaction, so a person never exists without the way in that made them. A Telegram another person holds — or anyone at all, for an invite, which would make one Telegram two people — is refused (409) before anything is spent, so the code stays open for the Telegram it was meant for; the unique index decides a race the read missed. A person linking a second Telegram replaces their first row.

## Sessions

- The cookie is `ziggurat_session`: HttpOnly, Secure (always; localhost counts as secure in a browser), SameSite=Lax, on `SessionCookie:Domain` (the parent domain; empty for a host-only localhost cookie). A session is valid until revoked; the cookie asks for `Max-Age` 400 days, the most a browser keeps one. Last-seen is written at a five-minute grain, because the check runs on every proxied request. A session's `Device` is the user agent it signed in with.
- A session records the passkey that began it; `PasskeyStore.RevokeAsync` revokes the passkey and those sessions in one transaction, and a session `mint-session` issued has no passkey and survives it. `SessionStore` is the issuing seam, apart from the ceremonies, so a test stack signs a seeded person in without a ceremony.
- A session or passkey is revoked **named by its owner as well as its id** (`RevokeAsync(userId, id)`): the ids are on the account page, so they are not secrets. Revoking a session or a passkey there, and disabling a member, announces `SessionsEnded` so the chat hub closes the connections those sessions held (`.claude/rules/chat-hub.md`).
- A person's **last passkey cannot be revoked from their account page** (`PasskeyStore.RevokeOwnAsync`, counted in the revocation's transaction, 409, drawn with no button): with none left only an owner's recovery link lets them back in, as the sign-in refusal for an unknown passkey says (`PasskeyEndpoints.UnknownPasskey`).

## The account page

`/accounts/account` (anyone signed in):

- edits the person's own display name and avatar (`POST /account/profile`, held to `People/Profile.Read`): a name of 1–200 characters; an avatar that is empty (none), an `http(s)` address, or a path on the deployment's hosts starting with one `/`, with no whitespace, control character or `\` — every app draws it as an image, and a browser reads `//host` or `/\host` as somebody else's. `/accounts/me` and the session check read the row; the chat hub learns the name on its next connection.
- lists the person's passkeys and devices, each revocable (revoking a passkey asks first: `data-confirm`, the pages' script). Devices are named by `DeviceNames.Of` ("Chrome on Android"), the raw user agent in the title.
- adds a passkey (a registration for the signed-in person; no new session).
- mints a Telegram code on a button press rather than per view — only its hash is kept, so it is shown once — as `t.me/<Telegram:BotUsername>?start=<code>`, or bare when no bot is named.
- shows `scripts/enroll-voice.sh <user id>`.
- names who holds "sees all conversations" and whose agent "reads everyone's Sugary log" (`data-reads-sugary-logs`, a health record's readers), which everyone in the household may know.

**An installed app has no Back button or address bar**, so every page of this service says on itself what to do next: the chat client reaches this page from its avatar (`SignedInBadge` → `IAccountService.OpenAccount`, a full load with `returnUrl`), Sugary from its settings. The page's Back (`data-back`) goes to that `returnUrl` when `ReturnUrls.Allowed` says it is the deployment's, else to `/` of the page's host; every form action, 303, error page and the stale-form page carry `returnUrl` on (`AccountPage.Here`), so a save does not lose the way back.

**Sign out** is `POST /accounts/sign-out` (antiforgery; not refused for a suspended household — ending one's own session is all it does): it revokes the session, announces `SessionsEnded`, deletes the cookie (`SessionCookie.Clear`) and 303s to `sign-in?returnUrl=<back>&signedOut=1`. Revoking this device's session from its row, or the passkey that signed this device in, leaves the same way.

Members are managed in the chat client's admin area (`.claude/rules/admin-owner-pages.md`, "Members", which holds the last-owner, operator and group-code rules); `/accounts/members`, this service's retired members page, redirects there (`MembersPage`). No recovery link is minted for the operator: their way back is the container command, and an owner who could mint one could take the dashboard.

`/accounts/*` is not behind the proxy's session check — sign-in and enrollment open signed out — except the admin API, `/accounts/admin/*`, which is (on a channel of its own) and believes only that check's assertion (`.claude/rules/admin.md`). So `PageGuard` reads `ziggurat_session` itself: a signed-out page goes to sign-in and back, a signed-out `/accounts/api` call gets 401. It refuses a **suspended household**'s session first, as the session check does and in its words (`SessionCheck.Suspended`: the "Household suspended" page, or the notice as plain text to the add-passkey script), before antiforgery or the owners-only check, so nobody of a suspended household mints a link or group code, adds a passkey or changes a profile.

- Every POST, form or the add-passkey script's JSON, carries an ASP.NET antiforgery token (`RequestVerificationToken` header for the script): the Lax cookie keeps cross-site forms out, but every host under the parent domain is the same site, and these forms decide who gets in.
- The antiforgery cookie, `ziggurat_antiforgery`, is host-only on `/accounts` and **not** Secure — TLS ends at the proxy, so no request here is https, and antiforgery throws rather than issue an always-secure cookie on a plain one.
- Data-protection keys live beside the database (`keys/` in the volume), so a restart leaves open forms valid.
- A form that changed something answers 303 back to its page. The pages' CSP allows `form-action 'self'`.

## Disabled members

An owner disables a member through the admin API (`.claude/rules/admin-owner-pages.md`, "Members"): `User.DisabledAt` set and every session they hold revoked, in one transaction (`MemberStore.DisableAsync`). Nothing else is deleted or spent — passkeys, linked Telegram, voice profile, open links, memories, topics and log stay — because every store that vouches for a person refuses a disabled one itself, so re-enabling is clearing the column:

- **Sessions** (`SessionStore`): none issued (`IssueAsync` — sign-in, enrollment and `mint-session` alike), and `VerifyAsync`/`LiveAsync` answer nobody, so the session check (and the admin gate behind it), `/accounts/me` and the chat hub's per-turn refresh refuse them even for a session that raced the revocation. A re-enabled member's old sessions stay revoked. A disabled member signing in with their passkey is told so (`PasskeyEndpoints.DisabledAccount`, 403) only after the signature verified, so only whoever holds their passkey learns it; anything else refused still reads as an unknown passkey (`SignInOutcome`).
- **Links** (`LinkStore.FindOpenAsync`): a link naming a disabled person — recovery, a Telegram link code — opens nothing (the enrollment page is 410); one still in date opens again once they are re-enabled. **What an owner handed out** — an invite, a group code, another person's recovery link — opens only while its minter (`Link.CreatedBy`) is still an enabled owner of the household, or the operator (user's decision, 2026-11-05): a demoted owner's owner invite made somebody an owner after the fact. A link minted by nobody (the command) or by the person it names (their Telegram code, their web link) is left alone; one still in date opens again once its minter is an owner again.
- **Channel identities**: `resolve` answers 404 for them on every channel (voice falls to the household's guest, Telegram to a stranger), and the Telegram calls that find a person by their Telegram (`TelegramLinks.PersonAsync`: `web-link`; `TelegramIdOfAsync`: `of-account`) find nobody.
- **The agent** is told through its view: `AgentHouseholdConfig.DisabledPeople`, announced as `HouseholdConfigProtocol.PeopleChanged` on every change, so it refuses their turns and the fires their delegations stand behind at once (`.claude/rules/person-assertions.md`, "Disabled members"). That covers Telegram's cache: the bot keeps a sender's resolution, assertion and all, for `LinkCache:Linked` (5 minutes), and an assertion verifies for ten, so for up to five minutes the bot still forwards a disabled member's messages and the agent answers each with `ConversationGroup.DisabledAnswer` rather than running it.
- **Recovery links** are minted for nobody disabled, whoever asks: `LinkStore.MintRecoveryAsync` answers a `RecoveryMint` (`Minted`, `Nobody`, `Disabled`), so the admin API (members, and the operator's owner recovery), the Telegram `web-link` and `mint-link --user` each refuse a dead link and say why.
- **Dreaming** and extraction (`MemoryExtractionWorker`, for a turn admitted before the disabling) skip them by the same view's `DisabledPeople`, though the `people` view still lists them (`.claude/rules/memory-architecture.md`); their memories are kept as they are.

## The session check

`GET /accounts/verify?channel=<id>`, for `forward_auth` alone: it asks `accounts:8080` on the compose network, and every host that routes `/accounts/*` answers `/accounts/verify*` 404 (`.claude/rules/admin.md`, "The proxy"), so no browser script reads an assertion out of its answer.

- A valid session answers 200 with `X-Ziggurat-User`, `X-Ziggurat-Tenant`, `X-Ziggurat-Roles` (`owner`|`member`, plus `operator`, plus `sees-all` for whoever holds "sees all conversations") and `X-Ziggurat-Assertion`, **always all four** (`ForwardedAccount.Headers`; every `copy_headers` copies all four, conformance-checked).
- The assertion is those three signed (see Assertions), with the session's id as `sid`, for the `channel` the block names — `signalr` on the chat client's routes, `sugary` on Sugary's, `dashboard` and `sexytime` on apps that start no turn, `admin` on this service's admin API — because the agent refuses a turn whose assertion names another channel than the one it came on. `channel` is required: a check without it is 400, so a new `forward_auth` block cannot hand an app an assertion naming nothing.
- Never add a site-level strip of `X-Ziggurat-*`: it runs after `forward_auth` and blanks what `copy_headers` set.
- Sees-all rides as a role so the chat hub learns it from the assertion it already verifies (`ForwardedAccount.SeesAll`, spelled again there as `SeesAllRole`). The hub reads roles once per connection, so a role or sees-all change is announced as `PeopleChanged`, the hub sends the household's connections `OnAccessChanged`, and the page renews its connection (`IChatLiveConnection.RenewAsync`, `AccessChangedEffect`) and reloads its list.
- `?role=<role>` answers a valid session lacking it 403 rather than sending a signed-in person to sign in again.
- A valid session of a **suspended household** (`Tenant.SuspendedAt`, `.claude/rules/admin-operator.md`) is checked first and answered 403 on every channel and role, never the four headers: a page (as below) gets a "Household suspended" page with `SessionCheck.SuspendedNotice` — `forward_auth` hands a non-2xx answer to the browser as it is, so the person reads why rather than looping through sign-in — and anything else the notice as plain text. The session is kept, so resuming lets it in again.
- With no session, a **page** — `X-Forwarded-Method` GET, an `X-Forwarded-Uri` whose first segment is not `api` or `hubs`, and an `Accept` naming `text/html` — gets a 302 to `{PublicBaseUrl}/accounts/sign-in?returnUrl=<the original, absolute>`; anything else gets 401, so an app's script can tell a signed-out call from content. Both are absolute because the protected page may be on another host than sign-in; `ReturnUrls.Resolve` follows a return only onto `AllowedOrigins`, else the front page.

## Who can forge the headers

Nobody, for the apps that act for a person: the chat hub, Sugary's API, the dashboard and the agent registration API believe only `X-Ziggurat-Assertion`, verified against this service's JWKS and held to their own channel, never the plain three (`.claude/rules/person-assertions.md`, "Apps behind the proxy"). The sandbox, the one container running what the model writes, is also kept off them by network: it runs in the tenant kit on a network shared with no other kit service, reaching the agent's bridge only through its tunnel (`.claude/rules/sandbox.md`, `TenantKitConformanceTests.TheSandbox_SharesANetworkWithNoOtherKitService`). The chat client's host (`webui`) serves files and reads no header; this service's own pages read their own cookie.

## SexyTime and the agent registration API

SexyTime (the `sexytime` upstream, not in this repo) is behind the same check: `sexy.*`'s catch-all carries the same `forward_auth` as the main host's chat client, and `sexy.*` routes `/accounts/*` too, unchecked, so a page there reads `/accounts/me` same-origin. The session cookie is on the parent domain and `https://sexy.…` is one of `AllowedOrigins`, so a signed-out page there goes to sign-in on the main host and comes back. The agent registration API (`/api/agents*`) and custom agents are `custom-agents.md`'s.

**An installed app's own files are served signed out** on the main host, Sugary's and SexyTime's (`@shell` in the Caddyfile, before the checked catch-all): any request that is not a page being opened (no `Sec-Fetch-Mode: navigate`, no `text/html` in `Accept`) and not under `/api/*` reaches `webui` / `mcp-sugary` / `sexytime` without the session check. A browser replaces a service worker by fetching its script and the files its shell caches, signed in or not, and workers shipped before sign-in existed answer every navigation — sign-in's included — from cache, so with those files checked such an installed app could never be replaced nor reach sign-in; Android also fetches an installed app's splash icon without the session. Pages and the APIs stay checked; `DeploymentConformanceTests.TheAppsOwnFiles_AreServedSignedOut_ButNoNavigationAndNoApi` and the smoke tier's `AnInstalledAppsFiles_AreServedSignedOut_ButNotItsPagesOrItsApi` hold it. The dashboard's host does the same through a matcher on its own check (`forward_auth @checked`: everything but such a request, and never `/api`, `/hubs` or `/accounts`), its own gate letting such a request through only for a file of its web root (`.claude/rules/observability.md`).

## Who is signed in

`GET /accounts/me` answers a valid session with JSON `{ userId, tenantId, displayName, avatarUrl, roles }`, `no-store`, and anything else with 401 — never a redirect, since the asker is an app's script. It sits under `/accounts/*`, so it is **not** behind the session check: the chat client reaches it same-origin on the main host with the session cookie riding along; another host's app needs that route too, or asks across origins with credentials. The chat client names and draws its person from it, and a 401 is what sends the page to sign in.

## Assertions

`Signing/Assertions` signs `IdentityClaims` as `TokenIssuers.Accounts` for `TokenAudiences.Assertion` — one audience for every consumer, since the service signs before it knows which the request goes to; the channel binds an assertion to a use — for `AssertionLifetime` (10 minutes, appsettings), on `AccountsClock`.

- The key is `SigningKeyStore` over `Signing` (`SigningKeySettings`): with `Signing:KeyDirectory` empty it is `signing/` beside the database, generated on the first assertion or JWKS request and kept in the volume (`keys/` beside it is data protection's). `SIGNING__PRIVATEKEY` hands a test stack a fixed key, which nothing writes, rotates or retires. Nothing about it is in `.env`.
- The public keys are `GET /accounts/internal/jwks` (`InternalApi.JwksPath`), the one internal route **outside** the caller filter — public keys are no secret and their verifiers hold no `CALLERS` entry — kept off the internet by the proxy's `/accounts/internal/*` 404 alone (`DeploymentConformanceTests.TheAccountsJwks_IsRefusedThroughTheProxyOnEveryHost`).
- Losing the key costs a moment: no assertion is stored, and the next one is signed with the new key.

## Ceremonies

Fido2 (`Fido2`/`Fido2.AspNet`): discoverable credentials (resident key required), user verification preferred, attestation none, the relying party the parent domain (`WebAuthn:RelyingPartyId`) and `AllowedOrigins` the accepted origins. Sign-in sends no allowed credentials; the assertion's user handle — the UTF-8 of the user id — says whose passkey it is. A ceremony's options are held server-side between its two requests (`CeremonyStore`, in memory, five minutes, taken once), because the finish must be checked against the challenge the server issued.

- The pages share one shell (`AccountsPages.Page`) in the chat's colours, whose head script (`/accounts/theme.js`) takes the chat's stored `theme` where the page shares its host; glyphs are inline SVG only (the CSP fetches nothing else).
- Sign-in carries `data-autostart` unless `signedOut=1`; the script offers the passkey at once **only in an installed app** (`display-mode: standalone`) and at most once a minute (sessionStorage), a refused or dismissed auto-start putting the button back without a word. A failed sign-in opens its "No passkey on this device?" help (another device, or an owner's recovery link). A spent link's page offers sign-in.
- The pages are server-rendered with one script (`PasskeyScript`), which converts base64url by hand rather than with `parseCreationOptionsFromJSON`, absent from older phone browsers. They send a strict CSP and `no-referrer`, since the enrollment page's address carries its token.

## Commands

`docker exec accounts dotnet Accounts.dll <verb>`. As in Sugary, `Program` detects a verb before building and runs it **instead of** the web server, with the verb's arguments kept out of configuration; every verb is handed the input beside its output (`docker exec -i`).

- `mint-link --operator [--name <display name>]`: creates the operator as an owner of the home household the first time, a recovery link for them every later time. `mint-link --user <id>`.
- `mint-session --user <id>`: prints only the cookie's value.
- `create-user --name <display name> [--role owner|member]`: a person of the home household with no passkey yet, a member unless named an owner — the E2E stack's way to seed the people it then signs in by `mint-session`.
- `mint-link` and `create-user` print the user and household ids in the same words (`user <id>, household <id>`).
- `rotate-signing-key` (a new key signs from the running service's next assertion on; the previous stays published) and `retire-signing-keys` (unpublishes all but the signing key; run once the previous key's last assertion has run out, which the rotation prints: the lifetime plus 30 s of skew). The two key verbs touch no database and, with `SIGNING__PRIVATEKEY` set, change nothing and exit 1.
- Tunnel verbs (`tunnel-credentials.md`): `mint-tunnel-credential --household <id> --server <name>` (prints `credential <id>` and, last and alone on its line, the credential, never shown again; 2 for an unreadable name, 1 for a household that does not exist), `list-tunnel-credentials` (each credential's id, server, household, when minted and live or revoked; never a secret), `revoke-tunnel-credential --id <id>` (revokes and tells the relay; 1 when it could not).
- `get-household-config`, `set-household-config` and `rewrap-household-keys` are `.claude/rules/household-config.md`'s; the last two read the input.

## Configuration and time

- Per-deployment values are compose environment entries, not secrets: `WEBAUTHN__RELYINGPARTYID`, `ALLOWEDORIGINS__n`, `SESSIONCOOKIE__DOMAIN`, `PUBLICBASEURL` (links and the sign-in redirect are built on it); `DeploymentConformanceTests` checks they describe hosts the Caddyfile serves. Missing ones refuse startup.
- `Telegram:BotUsername` (`TELEGRAM__BOTUSERNAME`, a compose entry of `accounts`) is per-deployment and optional: empty in appsettings, and the shipped compose names the deployment's shared bot. Empty shows the bare code, which is safe; a wrong or placeholder name would hand every code to whoever owns that bot.
- Its one secret is households' configuration master key (`HOUSEHOLDCONFIG__MASTERKEY`, `.claude/rules/household-config.md`); its signing key is generated, not provisioned, and its `CALLERS__<host>` are its callers' existing credentials (`channel-identity.md`), which a host run takes from the launch configuration as `local-mcp-secret`.
- `AGENTKEYSURL` is where the agent publishes the keys its call tokens verify with, for the configuration's proofs. `AGENTURL` is where the agent answers, for the operator's household list to ask its relay which of a household's servers are connected (`ConnectedServersClient`, with `CALLERS__agent` as its credential).
- `REDIS__CONNECTIONSTRING` is the deployment's Redis, on the existing `REDIS_PASSWORD`, for announcements alone — a revoked tunnel credential, a saved household configuration (`RedisAnnouncer`); nothing is stored there.
- Its clock is `AccountsClock`, a type of its own rather than the container's `TimeProvider`, so a test moves the service's time without moving Kestrel's.

## Tests

`AccountsServer` boots the shipped composition on a real localhost port with its own database, a signing key generated beside it, and a `FakeTimeProvider`; `VerifyAssertionAsync` checks an assertion as a consumer will, with `TokenVerifier` over a `JwksCache` fetching the service's own JWKS. The ceremony suite drives Chromium with CDP's virtual authenticator (ctap2, internal, resident key, user verification); the authenticator belongs to the page, so a sign-in after an enrollment runs on the same page with the cookies cleared. A browser signs in without a ceremony by being handed a `mint-session` cookie (Secure is accepted on localhost). `AccountsServer.SignedIn(session)` is the pages' HTTP client: it carries the session and antiforgery cookies and posts a form with the token its page rendered. The Telegram and tunnel-credential suites are in `channel-identity.md` and `tunnel-credentials.md`.
