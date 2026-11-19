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
  - "WebChat.Client/State/Effects/Household*Effect.cs"
  - "WebChat.Client/Services/AdminService.cs"
  - "WebChat.Client/Contracts/IAdminService.cs"
  - "Dashboard.Client/Services/HouseholdsAdminService.cs"
  - "Tests/Integration/Accounts/AdminApiTests.cs"
  - "Tests/E2E/WebChat/Household*E2ETests.cs"
---

# The admin API and the admin area

See also: `admin-keys.md` (keys and probes), `admin-owner-pages.md` (agents, members, features and
spaces, own servers, usage), `admin-operator.md` (the household list and suspension).

Owners run their household from an **admin area** in the chat client; the operator runs every household
from the dashboard (ADR 0008: each client its own pages). Both call one **admin API** on the accounts
service (`.scratch/tenants-admin/spec.md`). Service contracts: `.claude/rules/accounts.md`; the
configuration it edits: `.claude/rules/household-config.md`.

## Routes

Every path is spelled once, in `Domain/DTOs/Admin/AdminProtocol.cs`, read by the service and both clients.
Two roots, nothing else under `/accounts/admin`:

| Root | Who | Acts on |
|---|---|---|
| `/accounts/admin/household/*` (`AdminProtocol.Household`) | an **owner** | the household the caller's assertion names (`AdminCaller.HouseholdId`, its `tid`) — **no route under it takes a household id**, so none can be pointed at another household |
| `/accounts/admin/operator/*` (`AdminProtocol.Operator`) | the **operator** | any household, named in the path: `/accounts/admin/operator/households/{householdId}/...` |

The operator is also an owner of the home and manages it through the household routes. Here:
`GET`/`PUT /household/settings` (`HouseholdSettings`), the change logs `GET /household/changes?limit=` and
`GET /operator/changes?household=&limit=` (every household's, or one's). Keys: `admin-keys.md`; features,
spaces, agents, members, own servers, usage: `admin-owner-pages.md`; the household list and suspension:
`admin-operator.md`.

A route is mapped in `AdminApi.Map` into one of the two groups, its path from `AdminApi.Under(root,
AdminProtocol.X)`, and reads its caller as a bound `AdminCaller` (never null: the gate answered first). A
route with an id has its template there too (`AdminProtocol.XRoute`) beside the builder the clients call
(`AdminProtocol.X(id)`), which escapes every id; a service never writes a template by hand.

## The gate

`AdminApi.UseGate` is middleware in front of **every path under `/accounts/admin`**, ahead of routing's
endpoints (not a filter: minimal API binding runs before filters, so the caller would bind null). A route
added later is covered unasked; a path under neither root is 404. In order:

1. **The admin client's header**: `X-Requested-With: ziggurat-admin` (`AdminProtocol.RequestHeader`), else
   **403**. The API's CSRF defence, chosen over antiforgery tokens: no form to carry a token, a cross-site
   form cannot set a header, and a cross-origin script setting one is preflighted, which nothing answers.
   Every host under the parent domain is the same site, so the Lax cookie alone does not keep a sibling
   host's page out. A write's body is JSON (`[FromBody]` answers anything else 415).
2. **The assertion**: `X-Ziggurat-Assertion`, verified by the service's own `AssertionVerifier` over its own
   `SigningKeyStore` (on `AccountsClock`, shared skew), for **`AppChannels.Admin`** (`admin`), naming a
   person — else **401**. Never the plain `X-Ziggurat-*` headers, never the session cookie (the gate does
   not read it). The channel is the API's own, so an assertion for the hub, Sugary, the dashboard or
   SexyTime opens nothing here, and only the accounts service ever receives an `admin` one (the proxy copies
   it onto the request). That holds only because `/accounts/verify*` is never routed ("The proxy").
3. **The root's role**: `owner` (`ForwardedAccount.OwnerRole`) for `/household`, `operator` for
   `/operator`, else **403** with an `AdminRefusal`.

Every admin answer is `Cache-Control: no-store`. Liveness is the session check's, which ran on this very
request (forward_auth); a suspended household and disabled members are refused there, not here.

## Answers

- A read: **200** with its shape (`HouseholdSettings`, a list of `AdminChangeEntry`).
- A change that stood: **200** `AdminSaved<T> { value, warning }` — what is saved now, read back, and a
  warning when it stood with a caveat: readers not told (`AdminSections.NotToldWarning`, the one wording for
  every section and the members' changes; keys, own servers and suspension word their own), or a key saved
  while its provider could not be asked. A section's save answers through `AdminSections.AnswerAsync` (its
  rules' refusal 400, no household 404, the route's answer when it stood); a summary's changed fields are
  `AdminLog.Changed`.
- A change refused: **400** `AdminRefusal { message, problems }`, nothing changed or recorded; problems are
  the section rules' own words (`SectionSchema.Problems`), never a value sent.
- **401** signed out (session check's or gate's), **403** `AdminRefusal` not allowed, **404** no such route
  or household, **503** `AdminRefusal` what the deployment cannot do just now (keys with no master key,
  usage with no Observability host), read or save alike.

## Two owners with the same page open

A page saving a whole section — settings, features, spaces — or an agent's name and instructions sends
what it loaded beside what it wants (`AdminEdit.Of`, the body's `loaded`; `AgentEdit.Loaded`). The route
lays only what the page changed over the section as it is now (`AdminSections.Merged` over `EditMerge`): a
field left as loaded takes the stored value, so another owner's change stands; spaces merge by slug. A
field both changed differently — or a space one removed and the other changed — is **409** `AdminRefusal`
naming it ("space attic", "the time zone", "its name"), nothing saved or recorded; the chat client shows it
as a refusal (`AdminService`). A body with nothing loaded (operator tools, tests, older clients) is saved as
sent. User chose merging per item over refusing every stale save (2026-11-04). Keys, members and servers
routes change one item each and need none of it.

**A household's changes run one at a time** (`OneChangeAtATime`, a filter on the owners' group for every
method but GET/HEAD): each change reads, lays itself over and saves its section whole — a key save probes
in between — so two in flight saved over each other. A slow probe (at most `KeyProbes:Timeout`) delays
another owner's save of the same household; the operator's routes and the container's commands are not
queued.

**The spaces page keeps each space's loaded order** (`SpaceDrafts`: a move swaps two, an added space goes
last): renumbering made one owner's removal a change to every later space, so another owner's removal of a
different space was refused 409.

## Configuration is saved as the operator's command saves it

A section-changing route saves through `HouseholdConfigStore.SaveAsync(household, schema, document)` with
the section's `HouseholdSections.*Schema` — the same migration, rules, sealing and announcement as
`set-household-config` — so a value is held to one standard whoever sets it, and the save reaches the agent
(`HouseholdConfigSaved` on Redis; readers drop what they kept). A route edits its part and saves the
section whole; it never writes the store's rows itself. A **secret is never answered**: a keys route
answers a key's state and last characters, never the key, sealed or not.

## Every change is recorded

`AdminLog` (`AdminChanges` table: the actor's user and own household; the household changed; the route as
`"{METHOD} {path}"`; a summary; when, on `AccountsClock`). A change route calls `log.RecordAsync(http,
caller, summary, ct)` (household routes) or `RecordAsync(http, caller, householdId, summary, ct)`
(operator routes) **after** its change stood — a refusal records nothing.

- A **summary names what changed, never a value**: "Changed the name, time zone", "Replaced the OpenRouter
  key" — never any characters of a key, token or link.
- Minting something (a link, a credential) is a recorded change; the minted thing is never in the summary.
- Reads (`ListAsync`) are newest first, `limit` default 50, capped 200, each with the actor's current
  display name.

## The proxy

`DockerCompose/caddy/Caddyfile`'s main host: `handle /accounts/admin/*` to `accounts:8080` behind
`forward_auth` with `uri /accounts/verify?channel=admin` and the four `copy_headers`, **after** the
`/accounts/internal/*` refusal and **before** the unchecked `/accounts/*`. The session check takes any
non-empty channel, so `admin` needed no list.

- SexyTime's and Sugary's hosts, which route `/accounts/*`, answer `/accounts/admin/*` with `respond 404`:
  nothing there calls it, and their `/accounts/*` would hand it on unchecked.
  `DeploymentConformanceTests.TheAdminApi_IsReachedThroughItsSessionCheckAlone` holds every host to the
  checked route or none; the E2E stack's Caddyfile (`WebChatStack`) mirrors the main host.
- The **dashboard's host** routes the operator's routes alone, `handle /accounts/admin/operator/*`, with an
  inner `forward_auth` for `channel=admin` (the host-level `role=operator&channel=dashboard` check runs
  first; the inner `copy_headers` sets the admin assertion over the dashboard's), proxied to
  `accounts:8080`, after its own `/accounts/internal/*` refusal. Owners' routes there fall to the dashboard
  and are refused. `DashboardAdminE2EFixture` mirrors that host.

**The session check is never routed.** Every host routing `/accounts/*` to this service (main, SexyTime's,
Sugary's) answers `handle /accounts/verify*` with `respond 404`, beside and after its `/accounts/internal/*`
refusal: otherwise a same-origin script could ask `/accounts/verify?channel=admin` (or any channel) with the
cookie and read a ten-minute assertion out of the answer's headers. `forward_auth` asks `accounts:8080` on
the compose network and never comes this way; no client calls it. The dashboard's host routes no
`/accounts/*`, so the path falls to the dashboard.
`DeploymentConformanceTests.TheSessionCheck_IsAnswered404OnEveryHostThatRoutesTheAccountsService`;
`WebChatStack` mirrors the refusal.

## The admin area (chat client)

- **The way in** is `AdminEntry` in the chat header, drawn (`Icons.House`) beside the signed-in badge,
  shown only when `/accounts/me` names `owner` (`SignedInPerson.IsOwner`); never to a member, and the API
  refuses them anyway.
- **Its pages** are `/admin/<page>` — two segments, so no space slug (the chat's `/{Slug}`) is taken —
  each a component in `WebChat.Client/Pages/Admin/` with `@layout AdminLayout`, listed in
  `AdminArea.Pages` for the menu. `AdminLayout` draws its own header and menu, asks who is signed in
  (`LoadSignedIn`, handled by `UserIdentityEffect`; never `Initialize`, which starts the chat's live
  connection) and renders a page for an owner alone.
- **In and out by a full load** (`AdminArea.Enter`, `AdminArea.BackToChat`): the chat page starts its live
  connection once per load, and an in-app return — back button included — would start it twice.
- **State follows the client's pattern**: per page a store in `State/Admin/` (actions, a state holding only
  what the API answered, a reducer) and an effect in `State/Effects/` calling `IAdminService` and
  dispatching the outcome; signed out sends the page to sign in (`IAccountService.SendToSignIn`). Each page
  shows its outcome as an `AdminNotice` (`Components/Admin/AdminNoticeView`): saved as a status; a warning,
  a refusal with its problems, or unavailable as an alert. A page dispatches its load in `OnInitialized` and
  binds fields with `@bind:event="oninput"`.
- **`IAdminService` / `AdminService`** is the one client of the API: same-origin `HttpClient`, the admin
  header and `Accept: application/json` on every request, each answer an `AdminAnswer<T>`
  (`Answered(value, warning)`, `Refused(refusal)`, `SignedOut`, `NotAllowed(message)`,
  `Unreachable(message?)`, the message a 503's refusal). A new route is a method here; an effect settles
  its answer with `AdminAnswers.Settle` (`State/Admin/`), naming only its page's outcome and not-saved
  action. Stores, effects and the service are registered by `AddWebChatAdmin()`, apart from the chat's own,
  and each effect is activated in `Program.cs`.
- Icons are drawn (`.claude/rules/blazor-client-ui.md`).

## Tests

Seam 1, `AdminApiTests`: `AccountsServer.Admin(session)` reaches the API as the proxy would — the session
check asked for the admin channel first (its refusal is the answer), its four headers copied over the
request's, the admin header added. A raw `server.Client()` request sends what the proxy never would (the
cookie alone, plain headers, another channel's assertion, `TestAssertions.Forged`). Another household and
its owner: `SeedHouseholdAsync(name, person)`. The turn side is the agent's reader against a real Redis
(`HouseholdConfigurationClient` + `HouseholdAgentSource`), as `HouseholdConfigurationClientTests` does.

Seam 2, `HouseholdAdminE2ETests` (the `Chat` collection): an owner from the stack's second household's
seeded pool (`fixture.PersonElsewhereAsync(name, "owner")`), signed in with
`CreatePageSignedInAsync(session)`.

- **E2E people are in the home household, whose turns every collection runs**: an E2E test changes nothing
  a turn reads (zone, language, feature, agent, key) of the home — only its name, put back — and leaves
  turn-visible effects to seam 1.
- The exception is a change every open page of the household hears — who its people are
  (`HouseholdMembersE2ETests`, `SeesAllWithdrawnE2ETests`: a renewed connection) or its agents
  (`HouseholdAdminE2ETests`: agents fetched again): it works in the stack's second household
  (`PersonElsewhereAsync`; the home's agents, a key of its own), where no other collection has a page open.
