---
paths:
  - "Accounts/**"
  - "Tests/Integration/Accounts/**"
  - "Tests/Integration/Fixtures/AccountsServer.cs"
  - "Agent/App/AgentApi.cs"
  - "Infrastructure/Clients/Accounts/**"
  - "Domain/Contracts/IAccountResolver.cs"
---

# The accounts service

`Accounts/` is the only owner of households (tenants), people (users), roles, passkeys, invites, recovery links and sessions (`.scratch/accounts/spec.md`). It is a plain ASP.NET Core host, **not an MCP server**: no `Mcp.Hosting`, no project references at all, built `FROM base-sdk` only for the SDK band. The container is `accounts`, on the compose network only; Caddy routes `handle /accounts/*` on the main host to it, and `handle` passes the path on unstripped, so every route is mapped under `AccountsApp.PathBase` (`/accounts`).

## The store

EF Core on SQLite in `./volumes/accounts` (owned by PUID/PGID through the `accounts-volume` one-shot, as Sugary's is), migrated and the home household seeded by a hosted service before the first request, and by every command before it runs — the operator's first command may come before the service ever started. **Nothing in the model or the migrations names SQLite**, so a hosted database is a provider swap:

- Keys are opaque generated strings (`Tokens.NewId`, 32 lowercase hex: no `:` to break a composite key elsewhere), never a name and never a generated integer, so no `Sqlite:Autoincrement`. Key columns carry a max length, which an indexable key needs on other providers.
- Instants are `DateTimeOffset` converted to UTC `DateTime` (`ConfigureConventions`), enums are strings, no raw SQL, no JSON columns.
- A migration is generated with `dotnet ef migrations add <Name> --project Accounts/Accounts.csproj --output-dir Store/Migrations`, and then **stripped of the provider's store types** — every `type: "TEXT", ` in `Up`/`Down` and every `.HasColumnType(...)` in the designer and the snapshot — so each provider maps a CLR type to its own default. `dotnet ef migrations has-pending-model-changes` must still say nothing changed, and `dotnet ef migrations script` emits the same SQL either way.

Every record carries its tenant even though only the home one exists. A user's channel identities (a numeric Telegram id, a voice profile) arrive as rows of their own pointing at the user, so `Users` never grows a column per channel.

## Secrets at rest

A link's token and a session cookie's value are bearer secrets: only their SHA-256 hash is stored (`Tokens.Hash`), so the database alone opens nothing. A session and a passkey also have an opaque `Id`, which is what a device list or a revoke names.

## Links

`LinkKind.Recovery` adds a passkey to a person who already exists; the operator's first enrollment is a recovery of an account with no passkey yet. `LinkKind.Invite` names a display name and role and creates the person on redemption, in the inviter's household. `LinkKind.TelegramLink` is the account page's code that links a person's Telegram, redeemed as the bot's start parameter (a 43-character base64url token, inside Telegram's 64 of `[A-Za-z0-9_-]`); it never opens the enrollment page — `LinkStore.Enrollable` is invite and recovery only. A link is good once for `LinkLifetimeDays` (7). It is checked when its page opens and when its ceremony begins, but **spent** only at the ceremony's finish, by a conditional update inside the transaction that adds the passkey (`LinkStore.SpendAsync`), so two ceremonies racing on one link cannot both write.

An invite's person does not exist until redemption, but the authenticator stores the user handle at the ceremony's start, so the user id is chosen then (`Tokens.NewId()`, held with the ceremony) and the person is created with it. `LinkStore.RedeemInviteAsync(db, linkId, userId)` spends the invite and creates the person **inside the caller's transaction**, and is the seam a redemption without a passkey uses: Telegram's start parameter finds the link with `FindOpenAsync(token, [LinkKind.Invite], …)` and records the chat's id in the same transaction, so a person never exists without the way in that made them.

## Sessions

The cookie is `ziggurat_session`: HttpOnly, Secure (always, since localhost counts as secure in a browser), SameSite=Lax, on `SessionCookie:Domain` (the parent domain; empty for a host-only localhost cookie). A session is valid until revoked; the cookie asks for `Max-Age` 400 days because that is the most a browser keeps one. Last-seen is written at a five-minute grain, because the check runs on every proxied request. A session records the passkey that began it; `PasskeyStore.RevokeAsync` revokes the passkey and those sessions in one transaction, and a session `mint-session` issued has no passkey and survives it. `SessionStore` is the issuing seam, apart from the ceremonies, so a test stack signs a seeded person in without a ceremony. A session's `Device` is the user agent it signed in with. A session or passkey is revoked **named by its owner as well as its id** (`RevokeAsync(userId, id)`): the ids are on the account page, so they are not secrets.

## The account and members pages

`/accounts/account` (anyone signed in) lists the person's passkeys and devices, each revocable, adds a passkey (a registration for the signed-in person; no new session), mints a Telegram code on a button press rather than per view — only its hash is kept, so it is shown once — shown as `t.me/<Telegram:BotUsername>?start=<code>` or bare when no bot is named, shows `scripts/enroll-voice.sh <user id>`, and names who holds "sees all conversations", which everyone in the household may know. `/accounts/members` (owners; a member gets 403) lists the household with role, operator flag and the permission, mints invites and recovery links (shown in the POST's answer, the only time they exist in clear), changes roles and grants or withdraws the permission. Every read and change is scoped to the viewer's household. The last owner cannot be demoted, checked inside the one `UPDATE` (`MemberStore.ChangeRoleAsync`) so two owners demoting each other cannot both pass. The members page mints no recovery link for the operator: their way back is the container command, and an owner who could mint one could take the dashboard.

`/accounts/*` is not behind the proxy's session check — sign-in and enrollment open signed out — so `PageGuard` reads `ziggurat_session` itself, sending a signed-out page to sign-in and back and a signed-out `/accounts/api` call a 401. Every POST, form or the add-passkey script's JSON, carries an ASP.NET antiforgery token (`RequestVerificationToken` header for the script): the Lax session cookie keeps cross-site forms out, but every host under the parent domain is the same site, and these forms decide who gets in. The antiforgery cookie, `ziggurat_antiforgery`, is host-only on `/accounts` and **not** Secure — TLS ends at the proxy, so no request here is https, and antiforgery throws rather than issue an always-secure cookie on a plain one. Data-protection keys live beside the database (`keys/` in the volume), so a restart leaves open forms valid. A form that changed something answers 303 back to its page. The pages' CSP allows `form-action 'self'`.

## The session check

`GET /accounts/verify`, for `forward_auth`. A valid session answers 200 with `X-Ziggurat-User`, `X-Ziggurat-Tenant` and `X-Ziggurat-Roles` (`owner`|`member`, plus `operator`), **always all three**. `?role=<role>` answers a valid session lacking it 403 rather than sending a signed-in person to sign in again. With no session, a **page** — `X-Forwarded-Method` GET, an `X-Forwarded-Uri` whose first segment is not `api` or `hubs`, and an `Accept` naming `text/html` — gets a 302 to `{PublicBaseUrl}/accounts/sign-in?returnUrl=<the original, absolute>`; anything else gets 401, so an app's script can tell a signed-out call from content. The redirect and the return are absolute because the protected page may be on another of the deployment's hosts than the one sign-in is served on; `ReturnUrls.Resolve` follows a return only onto `AllowedOrigins`, else the front page.

## SexyTime and the agent registration API

SexyTime (the `sexytime` upstream, not in this repo) is behind the same check: `sexy.*`'s catch-all carries the same `forward_auth` as the main host's chat client, and `sexy.*` routes `/accounts/*` too, unchecked, so a page there reads `/accounts/me` same-origin. The session cookie is on the parent domain and `https://sexy.…` is one of `AllowedOrigins`, so a signed-out page there goes to sign-in on the main host and comes back.

`/api/agents*` is **not** behind the check, on either host: its caller is SexyTime's server, which holds the agent API's secret (`AGENTAPI__SHAREDSECRET`). The API takes the person from `X-Ziggurat-User` (exactly one value, else 400) and never reads a `userId` parameter; a custom agent is listed, used and deleted only under the id it was registered with. Because the proxy does not check this route, nothing strips an `X-Ziggurat-User` a client wrote — `copy_headers` only overwrites where `forward_auth` runs. The header is therefore trusted **only together with the secret**: its one holder is SexyTime's server, which copies the header off the signed-in request the check handed it. Handing the secret to anything that relays a client's headers unchecked would let that client name anyone. Custom agents stay in memory; making them durable is the Tenants phase.

## Who is signed in

`GET /accounts/me` answers a valid session with JSON `{ userId, tenantId, displayName, avatarUrl, roles }`, `no-store`, and anything else with 401 — never a redirect, since the asker is an app's script. It sits under `/accounts/*`, so it is **not** behind the session check: the chat client reaches it same-origin on the main host, where the proxy routes `/accounts/*`, with the session cookie riding along; another host's app needs that route too, or asks across origins with credentials. The chat client names and draws its person from it, and a 401 is what sends the page to sign in.

## Resolving a channel identity

`POST /accounts/internal/resolve` (`Internal/IdentityResolution`) is how a channel server learns whose account the identity it saw is: `{ channel, identity }` answered `{ tenantId, userId, displayName }`, or 404 for an identity nobody holds. `signalr` and `voice` identities are the account's own id (the chat client's turns carry the id the session check named; a voice profile is named after its user id), `telegram` is a linked numeric id and answers 404 until the bot writes the linked-identity rows, and any other channel is 400. It is **server to server only**:

- A caller presents `Authorization: Bearer <credential>`, matched against every `Callers` entry (`CALLERS__<host>`), and anything else is 401 before a row is read. The credential is the one the caller already holds for its own `/mcp` — compose feeds `CALLERS__mcp-channel-signalr` the same `${MCP_SECRET_CHANNEL_SIGNALR}` that server's `MCP__SHAREDSECRET` reads, and likewise Telegram, voice and Sugary — so no secret exists for this alone; `DeploymentConformanceTests` holds each entry to its server's variable. An empty entry admits nobody. Any listed caller may ask about any channel: Sugary is a caller and not a channel, and nothing here needs a caller confined to its own.
- The comparison is `Internal/CallerCredential`, `Domain.Security.SharedSecret`'s rule spelled again because this service references no project; `CallerCredentialTests` holds the two to the same answers.
- Every Caddy host that reaches this service answers `handle /accounts/internal/*` with `respond 404` **before** `/accounts/*` (conformance-checked), so a browser never reaches it; a new host routing `/accounts/*` needs the same block.

The client every channel server shares is `IAccountResolver` (`Domain/Contracts`), implemented by `AccountsResolverClient` (`Infrastructure/Clients/Accounts`, registered by `AddAccountResolver(config, ownMcpCredential)`, `Accounts:BaseUrl` defaulting to `http://accounts:8080`): null is nobody, and a call the service could not answer throws, so a caller never takes an outage for a stranger.

## Ceremonies

Fido2 (`Fido2`/`Fido2.AspNet`): discoverable credentials (resident key required), user verification preferred, attestation none, the relying party the parent domain (`WebAuthn:RelyingPartyId`) and `AllowedOrigins` the ceremony's accepted origins. Sign-in sends no allowed credentials, and the assertion's user handle — the UTF-8 of the user id — says whose passkey it is. A ceremony's options are held server-side between its two requests (`CeremonyStore`, in memory, five minutes, taken once), because the finish must be checked against the challenge the server issued. The pages are server-rendered with one script (`PasskeyScript`), which converts base64url by hand rather than with `parseCreationOptionsFromJSON`, absent from older phone browsers; they send a strict CSP and `no-referrer`, since the enrollment page's address carries its token.

## Commands

`docker exec accounts dotnet Accounts.dll <verb>`: `mint-link --operator [--name <display name>]` (creates the operator as an owner of the home household the first time, a recovery link for them every later time), `mint-link --user <id>`, `mint-session --user <id>` (prints only the cookie's value), `create-user --name <display name> [--role owner|member]` (a person of the home household with no passkey yet, a member unless named an owner: the operator's way to add someone before the members page exists, and the E2E stack's way to seed the people it then signs in by `mint-session`). As in Sugary, `Program` detects a verb before building and runs it **instead of** the web server, with the verb's arguments kept out of configuration. `mint-link` and `create-user` print the user and household ids in the same words (`user <id>, household <id>`), the only public way to learn them before the members page exists.

## Configuration and time

Per-deployment values are compose environment entries, not secrets: `WEBAUTHN__RELYINGPARTYID`, `ALLOWEDORIGINS__n`, `SESSIONCOOKIE__DOMAIN`, `PUBLICBASEURL` (links and the sign-in redirect are built on it); `DeploymentConformanceTests` checks they describe hosts the Caddyfile serves. Missing ones refuse startup. `Telegram:BotUsername` (`TELEGRAM__BOTUSERNAME`) is per-deployment too, but optional: empty shows the bare code. The service has no secret of its own; its `CALLERS__<host>` are its callers' existing credentials (above), and a host run takes them from the launch configuration as `local-mcp-secret`. Its clock is `AccountsClock`, a type of its own rather than the container's `TimeProvider`, so a test moves the service's time without moving Kestrel's.

## Tests

`AccountsServer` boots the shipped composition on a real localhost port with its own database and a `FakeTimeProvider`; the ceremony suite drives Chromium with CDP's virtual authenticator (ctap2, internal, resident key, user verification). The authenticator belongs to the page, so a sign-in after an enrollment runs on the same page with the cookies cleared. A browser signs in without a ceremony by being handed a `mint-session` cookie (Secure is accepted on localhost). `AccountsServer.SignedIn(session)` is the pages' HTTP client: it carries the session and antiforgery cookies and posts a form with the token its page rendered.
