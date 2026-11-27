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
service. Service contracts: `.claude/rules/accounts.md`; the configuration it edits:
`.claude/rules/household-config.md`.

## Routes

Every path is spelled once, in `Domain/DTOs/Admin/AdminProtocol.cs`, read by the service and both clients.
Two roots, nothing else under `/accounts/admin`:

| Root | Who | Acts on |
|---|---|---|
| `/accounts/admin/household/*` (`AdminProtocol.Household`) | an **owner** | the household the caller's assertion names (`AdminCaller.HouseholdId`) — **no route under it takes a household id** |
| `/accounts/admin/operator/*` (`AdminProtocol.Operator`) | the **operator** | any household, named in the path: `/accounts/admin/operator/households/{householdId}/...` |

The operator manages the home through the household routes. A route is mapped in `AdminApi.Map` into one
of the two groups, its path from `AdminApi.Under(root, AdminProtocol.X)`, and binds its caller as
`AdminCaller`. A route with an id has its template beside the builder the clients call
(`AdminProtocol.XRoute` / `AdminProtocol.X(id)`, which escapes every id); never write a template by hand.

## The gate

`AdminApi.UseGate` is **middleware** in front of every path under `/accounts/admin`, ahead of routing's
endpoints — not a filter, because minimal API binding runs before filters and the caller would bind null.
A path under neither root is 404. In order:

1. **The admin client's header**: `X-Requested-With: ziggurat-admin` (`AdminProtocol.RequestHeader`), else
   **403** — the API's CSRF defence, chosen over antiforgery tokens; the Lax cookie alone does not keep a
   sibling host's page out. It holds only while a cross-origin script setting the header is preflighted
   and nothing answers the preflight: the accounts service registers no CORS (`AdminProtocol.RequestHeader`'s
   comment). A write's body is JSON (415 otherwise).
2. **The assertion**: `X-Ziggurat-Assertion`, verified by the service's own `AssertionVerifier` over its own
   `SigningKeyStore`, for **`AppChannels.Admin`** (`admin`), naming a person — else **401**. Never the plain
   `X-Ziggurat-*` headers, never the session cookie. Only the accounts service ever receives an `admin`
   assertion — which holds only because `/accounts/verify*` is never routed ("The proxy").
3. **The root's role**: `owner` (`ForwardedAccount.OwnerRole`) for `/household`, `operator` for
   `/operator`, else **403** with an `AdminRefusal`.

Every admin answer is `Cache-Control: no-store`. Suspended households and disabled members are refused
by the session check, not here.

## Answers

- A read: **200** with its shape.
- A change that stood: **200** `AdminSaved<T> { value, warning }` — the value read back, and a warning when
  it stood with a caveat (`AdminSections.NotToldWarning`, the one wording for every section and members;
  keys, own servers and suspension word their own). A section's save answers through
  `AdminSections.AnswerAsync`.
- A change refused: **400** `AdminRefusal { message, problems }`, nothing changed or recorded; problems are
  the section rules' own words (`SectionSchema.Problems`), never a value sent.
- **401** signed out, **403** `AdminRefusal` not allowed, **404** no such route or household, **409** a
  stale-save conflict (below), **503** `AdminRefusal` what the deployment cannot do just now (keys with no
  master key, usage with no Observability host), read or save alike.

## Two owners with the same page open

A page saving a whole section — settings, features, spaces, app asks — or an agent's name and instructions sends
what it loaded beside what it wants (`AdminEdit.Of`, the body's `loaded`; `AgentEdit.Loaded`), and the route
lays only what changed over the section as it is now (`AdminSections.Merged` over `EditMerge`; spaces merge
by slug, app caps by app id, a removed one's `null` dropped). A field both changed differently — or a space one removed and the other changed — is **409**
`AdminRefusal` naming it, nothing saved or recorded; the chat client shows it as a refusal (`AdminService`). A body with
nothing loaded is saved as sent. User chose merging per item over refusing every stale save (2026-11-04).
Keys, members and servers routes change one item each and need none of it.

**A household's changes run one at a time** (`OneChangeAtATime`, a filter on the owners' group for every
method but GET/HEAD); the operator's routes and the container's commands are not queued, but for the apps
feature's, which saves the section an owner's features save saves whole and so takes the household's turn
(`OneChangeAtATime.InTurnAsync`, by the path's household).

**The spaces page keeps each space's loaded order** (`SpaceDrafts`), because the server merges per space:
renumbering would make one owner's removal a change to every later space and refuse another's 409.

## Configuration is saved as the operator's command saves it

A section-changing route saves through `HouseholdConfigStore.SaveAsync(household, schema, document)` with
the section's `HouseholdSections.*Schema` — the same migration, rules, sealing and `HouseholdConfigSaved`
announcement as `set-household-config`. A route edits its part and saves the section whole; it never
writes the store's rows itself. A **secret is never answered**: a keys route answers a key's state and
last characters, never the key, sealed or not.

## Every change is recorded

`AdminLog` (`AdminChanges` table). A change route calls `log.RecordAsync(...)` **after** its change stood —
a refusal records nothing.

- A **summary names what changed, never a value** ("Replaced the OpenRouter key"; `AdminLog.Changed`) —
  never any characters of a key, token or link.
- Minting something (a link, a credential) is a recorded change; the minted thing is never in the summary.

## The proxy

`DockerCompose/caddy/Caddyfile`'s main host: `handle /accounts/admin/*` to `accounts:8080` behind
`forward_auth` with `uri /accounts/verify?channel=admin` and the four `copy_headers`, **after** the
`/accounts/internal/*` refusal and **before** the unchecked `/accounts/*`.

- SexyTime's and Sugary's hosts, which route `/accounts/*`, answer `/accounts/admin/*` with `respond 404`,
  since their `/accounts/*` would hand it on unchecked
  (`DeploymentConformanceTests.TheAdminApi_IsReachedThroughItsSessionCheckAlone`); the E2E stack's
  Caddyfile (`WebChatStack`) mirrors the main host.
- The **dashboard's host** routes the operator's routes alone, `handle /accounts/admin/operator/*`, with an
  inner `forward_auth` for `channel=admin` after the host-level `role=operator&channel=dashboard` check (the
  inner `copy_headers` sets the admin assertion over the dashboard's), after its own `/accounts/internal/*`
  refusal. `DashboardAdminE2EFixture` mirrors that host.

**The session check is never routed.** Every host routing `/accounts/*` (main, SexyTime's, Sugary's)
answers `handle /accounts/verify*` with `respond 404`, beside and after its `/accounts/internal/*`
refusal: otherwise a same-origin script could read an assertion out of its answer's headers.
`forward_auth` reaches `accounts:8080` on the compose network and never comes this way.
`DeploymentConformanceTests.TheSessionCheck_IsAnswered404OnEveryHostThatRoutesTheAccountsService`;
`WebChatStack` mirrors the refusal.

## The admin area (chat client)

- **The way in** is `AdminEntry` in the chat header (`Icons.House`), shown only when `/accounts/me` names
  `owner` (`SignedInPerson.IsOwner`).
- **Its pages** are `/admin/<page>` — two segments, so no space slug (the chat's `/{Slug}`) is taken —
  each in `WebChat.Client/Pages/Admin/` with `@layout AdminLayout`, listed in `AdminArea.Pages`.
  `AdminLayout` asks who is signed in with `LoadSignedIn`, never `Initialize` (which starts the chat's live
  connection), and renders a page for an owner alone.
- **In and out by a full load** (`AdminArea.Enter`, `AdminArea.BackToChat`), never in-app navigation, or the
  chat's live connection starts twice.
- **State follows the client's pattern**: per page a store in `State/Admin/` and an effect in
  `State/Effects/` calling `IAdminService`, settled with `AdminAnswers.Settle`; each page shows its outcome
  as an `AdminNotice`.
- **`IAdminService` / `AdminService`** is the one client of the API (admin header on every request, each
  answer an `AdminAnswer<T>`); a new route is a method here. Stores, effects and the service are registered
  by `AddWebChatAdmin()`, apart from the chat's own, and each effect is activated in `Program.cs`.
- Icons are drawn (`.claude/rules/blazor-client-ui.md`).

## Tests

Seam 1, `AdminApiTests`: `AccountsServer.Admin(session)` reaches the API as the proxy would (session check
for the admin channel, its four headers copied, the admin header added); a raw `server.Client()` request
sends what the proxy never would. Another household and its owner: `SeedHouseholdAsync(name, person)`. The
turn side is the agent's reader against a real Redis, as `HouseholdConfigurationClientTests` does.

Seam 2, `HouseholdAdminE2ETests` (the `Chat` collection): an owner from the stack's second household
(`fixture.PersonElsewhereAsync(name, "owner")`), signed in with `CreatePageSignedInAsync(session)`.

- **E2E people are in the home household, whose turns every collection runs**: an E2E test changes nothing
  a turn reads (zone, language, feature, agent, key) of the home — only its name, put back — and leaves
  turn-visible effects to seam 1.
- The exception is a change every open page of the household hears — its people
  (`HouseholdMembersE2ETests`, `SeesAllWithdrawnE2ETests`) or its agents (`HouseholdAdminE2ETests`): it works
  in the stack's second household (`PersonElsewhereAsync`), where no other collection has a page open.
