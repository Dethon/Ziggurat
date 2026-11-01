---
paths:
  - "Accounts/Admin/**"
  - "Domain/DTOs/Admin/**"
  - "Domain/Security/AppChannels.cs"
  - "WebChat.Client/Pages/Admin/**"
  - "WebChat.Client/Layout/AdminLayout.razor"
  - "WebChat.Client/Layout/AdminArea.cs"
  - "WebChat.Client/Components/AdminEntry.razor"
  - "WebChat.Client/Components/Admin/**"
  - "WebChat.Client/State/Admin/**"
  - "WebChat.Client/State/Effects/HouseholdSettingsEffect.cs"
  - "WebChat.Client/Services/AdminService.cs"
  - "WebChat.Client/Contracts/IAdminService.cs"
  - "Tests/Integration/Accounts/AdminApiTests.cs"
  - "Tests/E2E/WebChat/HouseholdAdminE2ETests.cs"
---

# The admin API and the admin area

Owners run their household from an **admin area** in the chat client, and the operator runs the
deployment's households from the dashboard (ADR 0008: each client its own pages, neither borrowing
from the other). Both call one **admin API** on the accounts service (`.scratch/tenants-admin/spec.md`).
The accounts service's own contracts are `.claude/rules/accounts.md`; the configuration it edits is
`.claude/rules/household-config.md`.

## Routes

Every path is spelled once, in `Domain/DTOs/Admin/AdminProtocol.cs`, which the service and both
clients read. Two roots, and nothing else under `/accounts/admin`:

| Root | Who | Acts on |
|---|---|---|
| `/accounts/admin/household/*` (`AdminProtocol.Household`) | an **owner** | the household the caller's assertion names (`AdminCaller.HouseholdId`, its `tid`) — **no route under it takes a household id**, so none can be pointed at another household |
| `/accounts/admin/operator/*` (`AdminProtocol.Operator`) | the **operator** | any household, named in the path: `/accounts/admin/operator/households/{householdId}/...` |

The operator is also an owner of the home household and manages the home through the household
routes like any owner. Today's routes: `GET`/`PUT /household/settings` (`HouseholdSettings`),
`GET /household/changes?limit=` (the household's change log) and `GET /operator/changes?household=&limit=`
(every household's, or one's). A route is mapped in `AdminApi.Map` into one of the two groups, with its
path inside the group from `AdminApi.Under(root, AdminProtocol.X)`, and reads its caller as a bound
`AdminCaller` parameter (never null: the gate answered first).

## The gate

`AdminApi.UseGate` is middleware in front of **every path under `/accounts/admin`**, ahead of routing's
endpoints (a filter cannot do it: minimal API binding runs before filters, so the caller would bind
null), so a route added later is covered without saying so and a path under neither root is 404. In
order:

1. **The admin client's header**: `X-Requested-With: ziggurat-admin` (`AdminProtocol.RequestHeader`),
   else **403**. This is the API's CSRF defence, chosen over the accounts pages' antiforgery tokens:
   the API renders no form to carry a token, a cross-site form cannot set a header, and a cross-origin
   script that sets one is preflighted, which nothing here answers. Every host under the parent domain
   is the same site, so the Lax session cookie alone does not keep a sibling host's page out. A write's
   body is JSON (`[FromBody]` binding answers anything else 415).
2. **The assertion**: `X-Ziggurat-Assertion`, verified by the service's own `AssertionVerifier` over its
   own `SigningKeyStore` (on `AccountsClock`, with the shared skew), for **`AppChannels.Admin`** (`admin`),
   naming a person — else **401**. Never the plain `X-Ziggurat-*` headers and never the session cookie:
   the gate does not read the cookie at all. The channel is the admin API's own, so an assertion signed
   for the hub, Sugary, the dashboard or SexyTime opens nothing here, and only the accounts service ever
   receives an `admin` one (the proxy copies it onto the request it hands this service).
3. **The root's role**: `owner` (`ForwardedAccount.OwnerRole`) for `/household`, `operator` for `/operator`,
   else **403** with an `AdminRefusal`.

Every admin answer is `Cache-Control: no-store`. The session's liveness is the session check's: it ran
on this very request (forward_auth), so the assertion is seconds old; suspension (09) and disabled
members (04) are refused there, not here.

## Answers

- A read answers **200** with its shape (`HouseholdSettings`, a list of `AdminChangeEntry`).
- A change that stood answers **200** `AdminSaved<T> { value, warning }`: what is saved now, read back,
  and a warning when it stood with a caveat — readers not told (`HouseholdSettingsAdmin.NotToldWarning`),
  or for 03 a key saved while its provider could not be asked.
- A change refused answers **400** `AdminRefusal { message, problems }`, nothing changed and nothing
  recorded; the problems are the section rules' own words (`SectionSchema.Problems`), never a value sent.
- **401** signed out (the session check's own answer, or the gate's), **403** `AdminRefusal` not allowed,
  **404** no such route or household.

## Configuration is saved as the operator's command saves it

A route that changes a section saves through `HouseholdConfigStore.SaveAsync(household, schema, document)`
with the section's `HouseholdSections.*Schema` — the same migration, rules, sealing and announcement as
`set-household-config` — so a value is held to one standard whoever sets it, and the save reaches the
agent (`HouseholdConfigSaved` on Redis, the readers drop what they kept). A route edits the part of a
section it is about and saves the section whole; it never writes the store's rows itself. A **secret is
never answered**: a keys route (03) answers a key's state and last characters, never the key, sealed
or not.

## Every change is recorded

`AdminLog` (`AdminChanges` table: who — the actor's user and their own household; the household changed;
the route as `"{METHOD} {path}"`; a summary; when, on `AccountsClock`). A change route calls
`log.RecordAsync(http, caller, summary, ct)` (household routes: the caller's household) or
`RecordAsync(http, caller, householdId, summary, ct)` (operator routes) **after** its change stood — a
refusal records nothing. A **summary names what changed, never a value**: "Changed the name, time zone"
for settings, "Replaced the OpenRouter key" for 03, never any characters of a key, a token or a link.
Reads (`ListAsync`) are newest first, `limit` defaulting to 50 and capped at 200, each with the actor's
current display name. Minting something (a link, a credential) is a change and is recorded; it is never
in the summary.

## The proxy

`DockerCompose/caddy/Caddyfile`'s main host routes `handle /accounts/admin/*` to `accounts:8080` behind
`forward_auth` with `uri /accounts/verify?channel=admin` and the four `copy_headers`, **after** the
`/accounts/internal/*` refusal and **before** `/accounts/*` (which is unchecked). The session check takes
any non-empty channel, so `admin` needed no list. The other hosts that route `/accounts/*` (SexyTime's,
Sugary's) answer `/accounts/admin/*` with `respond 404`: nothing there calls it, and their `/accounts/*`
would otherwise hand it on unchecked. `DeploymentConformanceTests.TheAdminApi_IsReachedThroughItsSessionCheckAlone`
holds every host to either the checked route or no route to the accounts service; the E2E stack's own
Caddyfile (`WebChatStack`) mirrors the main host's. **For 02**, the dashboard's host gains its own
`handle /accounts/admin/*` with a `forward_auth` for `channel=admin` inside it (the host-level
`role=operator&channel=dashboard` check runs first; the inner one's `copy_headers` sets the admin
assertion over it), proxied to `accounts:8080`, and the conformance test's main-host expectation widens
to it.

## The admin area (chat client)

- **The way in** is `AdminEntry` in the chat header, drawn (`Icons.House`) beside the signed-in badge
  and shown only when `/accounts/me` names the `owner` role (`SignedInPerson.IsOwner`); a member is never
  shown it, and the API refuses them anyway.
- **Its pages** are under `/admin/<page>` — two segments, so no space's slug (the chat's `/{Slug}`) is
  taken — each a component in `WebChat.Client/Pages/Admin/` with `@layout AdminLayout`, listed in
  `AdminArea.Pages` for the menu. `AdminLayout` draws its own header and menu, asks who is signed in
  (`LoadSignedIn`, handled by `UserIdentityEffect`; the area never dispatches `Initialize`, which starts
  the chat's live connection) and renders a page for an owner alone.
- **In and out by a full load** (`AdminArea.Enter`, `AdminArea.BackToChat`): the chat page starts its live
  connection once per load, and coming back to it in-app — the back button included — would start it
  twice.
- **State follows the client's pattern**: per page a store in `State/Admin/` (actions, a state holding
  only what the API answered, a reducer) and an effect in `State/Effects/` that calls `IAdminService`
  and dispatches the outcome; a signed-out answer sends the page to sign in (`IAccountService.SendToSignIn`).
  Each page shows its outcome as an `AdminNotice` (`Components/Admin/AdminNoticeView`): saved as a status,
  a warning, a refusal with its problems, or unavailable, as an alert. The page dispatches its load in
  `OnInitialized` and binds fields with `@bind:event="oninput"`.
- **`IAdminService` / `AdminService`** is the one client of the API: same-origin `HttpClient`, the admin
  header and `Accept: application/json` on every request, each answer an `AdminAnswer<T>`
  (`Answered(value, warning)`, `Refused(refusal)`, `SignedOut`, `NotAllowed(message)`, `Unreachable`).
  A new route is a method here. Stores, effects and the service are registered by `AddWebChatAdmin()`,
  apart from the chat's own, and each effect is activated in `Program.cs`.
- Icons are drawn (`.claude/rules/blazor-client-ui.md`).

## Tests

Seam 1, `AdminApiTests`: `AccountsServer.Admin(session)` reaches the API as the proxy would — the session
check asked for the admin channel first (its refusal is the answer), its four headers copied over the
request's, the admin header added; a raw `server.Client()` request is how a test sends what the proxy
would never pass (the cookie alone, plain headers, another channel's assertion, `TestAssertions.Forged`).
Another household and its owner are `SeedHouseholdAsync(name, person)`. The turn side is the agent's
reader against a real Redis (`HouseholdConfigurationClient` + `HouseholdAgentSource`), as
`HouseholdConfigurationClientTests` does. Seam 2, `HouseholdAdminE2ETests` (the `Chat` collection): an
owner is made by `create-user --role owner` and signed in with `CreatePageSignedInAsync(session)`.
**Every E2E person is in the home household, whose turns every collection runs**: an E2E test changes
nothing a turn reads (a zone, a language, a feature, an agent, a key) of the home — only its name, put
back — and leaves turn-visible effects to seam 1.
