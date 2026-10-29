---
paths:
  - "Accounts/**"
  - "Tests/Integration/Accounts/**"
  - "Tests/Integration/Fixtures/AccountsServer.cs"
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

`LinkKind.Recovery` adds a passkey to a person who already exists; the operator's first enrollment is a recovery of an account with no passkey yet. `LinkKind.Invite` (display name and role, creating the person on redemption) is modelled but not yet minted or redeemed — the members page brings both. A link is good once for `LinkLifetimeDays` (7). It is checked when its page opens and when its ceremony begins, but **spent** only at the ceremony's finish, by a conditional update inside the transaction that adds the passkey (`LinkStore.SpendAsync`), so two ceremonies racing on one link cannot both write.

## Sessions

The cookie is `ziggurat_session`: HttpOnly, Secure (always, since localhost counts as secure in a browser), SameSite=Lax, on `SessionCookie:Domain` (the parent domain; empty for a host-only localhost cookie). A session is valid until revoked; the cookie asks for `Max-Age` 400 days because that is the most a browser keeps one. Last-seen is written at a five-minute grain, because the check runs on every proxied request. A session records the passkey that began it; `PasskeyStore.RevokeAsync` revokes the passkey and those sessions in one transaction, and a session `mint-session` issued has no passkey and survives it. `SessionStore` is the issuing seam, apart from the ceremonies, so a test stack signs a seeded person in without a ceremony.

## The session check

`GET /accounts/verify`, for `forward_auth`. A valid session answers 200 with `X-Ziggurat-User`, `X-Ziggurat-Tenant` and `X-Ziggurat-Roles` (`owner`|`member`, plus `operator`), **always all three**. `?role=<role>` answers a valid session lacking it 403 rather than sending a signed-in person to sign in again. With no session, a **page** — `X-Forwarded-Method` GET, an `X-Forwarded-Uri` whose first segment is not `api` or `hubs`, and an `Accept` naming `text/html` — gets a 302 to `{PublicBaseUrl}/accounts/sign-in?returnUrl=<the original, absolute>`; anything else gets 401, so an app's script can tell a signed-out call from content. The redirect and the return are absolute because the protected page may be on another of the deployment's hosts than the one sign-in is served on; `ReturnUrls.Resolve` follows a return only onto `AllowedOrigins`, else the front page.

## Ceremonies

Fido2 (`Fido2`/`Fido2.AspNet`): discoverable credentials (resident key required), user verification preferred, attestation none, the relying party the parent domain (`WebAuthn:RelyingPartyId`) and `AllowedOrigins` the ceremony's accepted origins. Sign-in sends no allowed credentials, and the assertion's user handle — the UTF-8 of the user id — says whose passkey it is. A ceremony's options are held server-side between its two requests (`CeremonyStore`, in memory, five minutes, taken once), because the finish must be checked against the challenge the server issued. The pages are server-rendered with one script (`PasskeyScript`), which converts base64url by hand rather than with `parseCreationOptionsFromJSON`, absent from older phone browsers; they send a strict CSP and `no-referrer`, since the enrollment page's address carries its token.

## Commands

`docker exec accounts dotnet Accounts.dll <verb>`: `mint-link --operator [--name <display name>]` (creates the operator as an owner of the home household the first time, a recovery link for them every later time), `mint-link --user <id>`, `mint-session --user <id>` (prints only the cookie's value). As in Sugary, `Program` detects a verb before building and runs it **instead of** the web server, with the verb's arguments kept out of configuration. `mint-link` prints the user and household ids, the only public way to learn them before the members page exists.

## Configuration and time

Per-deployment values are compose environment entries, not secrets: `WEBAUTHN__RELYINGPARTYID`, `ALLOWEDORIGINS__n`, `SESSIONCOOKIE__DOMAIN`, `PUBLICBASEURL` (links and the sign-in redirect are built on it); `DeploymentConformanceTests` checks they describe hosts the Caddyfile serves. Missing ones refuse startup. The service needs no secret yet. Its clock is `AccountsClock`, a type of its own rather than the container's `TimeProvider`, so a test moves the service's time without moving Kestrel's.

## Tests

`AccountsServer` boots the shipped composition on a real localhost port with its own database and a `FakeTimeProvider`; the ceremony suite drives Chromium with CDP's virtual authenticator (ctap2, internal, resident key, user verification). The authenticator belongs to the page, so a sign-in after an enrollment runs on the same page with the cookies cleared.
