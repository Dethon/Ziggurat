---
paths:
  - "McpServerSugary/**"
  - "Sugary.Client/**"
  - "Domain/Tools/Sugary/**"
  - "Domain/Contracts/ISugaryLog.cs"
  - "Tests/Integration/Fixtures/SugaryServer.cs"
  - "Tests/Integration/McpServerTests/*Sugary*"
  - "Tests/Unit/Domain/Sugary/**"
---

# Sugary, per person

Sugary's log is SQLite in its own volume (`docs/adr/0043`); its channel is in `.claude/rules/channels.md` (dual-role servers) and its mount in `.claude/rules/virtual-filesystem.md`. This file is who the log belongs to (`.scratch/accounts/spec.md`, "Sugary").

## Who owns a row

Each person keeps a log of their own while the household shares its kitchen. Meals, doses, pens, periods, day notes, exercise sessions and push subscriptions are `IPersonal` (`UserId` and `TenantId`, both the accounts service's opaque ids); foods and recipes are `IHousehold` (`TenantId`). A day note's key is `(UserId, Day)`. A meal item's food or recipe id and a dose's meal ids stay plain values, not keys.

`SugaryDbContext` holds every query to one owner with **query filters** on its `Owner` (`Account?`), so no route, no mount read and no find, update or delete has a predicate of its own to forget. It is opened for a person by `SugaryStore.For(account)`. A row a person's context adds is stamped with that owner in `SaveChanges`. The factory's context (`IDbContextFactory`, `Owner` null) owns nothing: its filters match no row, and a row it adds must already name its owner or `SaveChanges` throws, because a row nobody owns would be kept and never seen. What needs every owner's rows says `IgnoreQueryFilters()` in so many words, and only these do: the pen reminders, the import's already-there check, and a push subscription changing hands.

## Rows from before

Every row from before the log was per person, and every row the `import` verb brings, carries **`legacy`** (`Owners.Unassigned`) in each owner column — spelled out in the `People` migration's defaults, which must never follow the constant. No account's id can be `legacy` (they are 32 lowercase hex), so those rows are **nobody's**: no request, no turn and no reminder sees them, the person they were for included. The household migration gives them to the person named `--sugary` (`HouseholdMigration/Steps/SugaryLog.cs`, `.claude/rules/household-migration.md`): `UserId`/`TenantId` set on every `IPersonal` table where `UserId = 'legacy'`, and `TenantId` on foods and recipes where it is `legacy`. `SugaryServer.GiveUnassignedRowsToAsync` is the suite's stand-in for that step. A legacy pen is reminded to nobody; a legacy push subscription is taken over by whoever next signs in on that phone, since the app saves its subscription on every open.

## The app and its API

The app has no sign-in. `sugary.*` in the Caddyfile puts the accounts service's session check in front of everything but `/mcp` (404) and `/accounts/*` (routed, so sign-in is same-origin; `/accounts/internal/*` is 404 first), and the check copies its four `X-Ziggurat-*` headers over any the client sent. The server believes only the signed one, `X-Ziggurat-Assertion`, verified against the accounts service's JWKS (`Accounts:JwksUrl`) and for channel `sugary` (`Person.SignedInAsync`, once per request in the gate in front of `/api`, `.claude/rules/service-identity.md`); the plain three are never read, and an `/api` call without a believable assertion is 401. So `localhost:6017` straight (the ports override, the E2E stack) needs an assertion the accounts service signed: ask `http://localhost:5004/accounts/verify?channel=sugary` with a `ziggurat_session` cookie from `mint-session` and send the headers it answers, as the dev server does.

- A request to `/api` without one `X-Ziggurat-Assertion` that verifies — the accounts service's signature, in date, for channel `sugary`, naming a person (the guest is nobody here) — is answered **401** by a middleware before any route binds, whatever plain headers ride beside it; that 401 is also the app's cue to sign in.
- A route's `SugaryDbContext` parameter is **bound**, not resolved: `SugaryDbContext.BindAsync` (in `App/PersonsLog.cs`) opens the store for the request's `Person` and disposes it with the response. Minimal APIs prefer `BindAsync` to the container for a parameter, so a route cannot be handed the unowned context. A route that needs the person itself (an estimate, a label, a push subscription) takes `Person` too.
- A push subscription's key is its endpoint, one browser: a browser that subscribes as somebody else becomes their phone (`UserId` reassigned), so her reminders stop reaching it.
- Foods' and recipes' use counts come from the asking person's own meals.

## Turns, the mount and reminders

- An estimate's and a label's turn carry `Principal("sugary", <user id>, Account)`, and their `Sender` is the account's id: the assertion names nothing more, and a resolution call per estimate would put the accounts service on every meal's path. They also carry that very assertion (`Person.Assertion`, the request's `X-Ziggurat-Assertion`) as the notification's `Assertion`, which is what the agent believes about the turn — and its own check is for channel `sugary` too. `OpenEstimates` remembers who opened a meal's conversation, and anyone else's answers on it are the same 410 as a closed one.
- The `/sugary` mount answers for the account on the principal the call's `_meta` carries (`SugaryFileSystem.For`, `Principal.Of(caller.Conversation)?.Account`), never an ambient. A call naming nobody — a voice guest, a fire with no person behind it, a call with no context — is refused `permission_denied`, never shown an empty log: "she logged nothing" would be a false fact about a health record. The byte read overrides `ReadBlobAsync` for that refusal, because one thrown from the chunk stream reaches the wire as a failure to retry. The eval stamps every turn with `EvalRun.Account` and seeds its log under it.
- `PenReminders` sends each due pen's reminder to its owner's phones only.

## The client

A 401 from any call sends the page to `/accounts/sign-in?returnUrl=<here>` (`signin.ts`) with a full load. The installed app's worker passes `/accounts/*` navigations to the network (`navigateFallbackDenylist`), or it would answer sign-in with the app's own page and loop, and the manifest is fetched with credentials (`useCredentials`), since the check stands in front of it too. Settings links to `/accounts/account`, where a device is signed out. `npm run dev` proxies `/api` as the proxy would: with `SUGARY_SESSION` set to a `mint-session` cookie, it asks the accounts service's session check (`ACCOUNTS_URL`, default `http://localhost:5004`) for `channel=sugary` before each call and copies the four headers it answers onto the request; unset, every call is 401.
