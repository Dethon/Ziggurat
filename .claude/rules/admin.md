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
  - "WebChat.Client/State/Effects/HouseholdAgentsEffect.cs"
  - "WebChat.Client/Services/AdminService.cs"
  - "WebChat.Client/Contracts/IAdminService.cs"
  - "Tests/Integration/Accounts/AdminApiTests.cs"
  - "Tests/Integration/Accounts/HouseholdAgentsAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdAdminE2ETests.cs"
  - "Accounts/Keys/**"
  - "WebChat.Client/State/Effects/HouseholdKeysEffect.cs"
  - "WebChat.Client/State/Effects/OnboardingEffect.cs"
  - "Infrastructure/Agents/ChatClients/RefusedKeyReportingChatClient.cs"
  - "Infrastructure/Agents/ChatClients/IHouseholdKeyReports.cs"
  - "Tests/Integration/Accounts/KeysAdminTests.cs"
  - "Tests/Integration/Fixtures/FakeKeyProviders.cs"
  - "Tests/E2E/WebChat/HouseholdKeysE2ETests.cs"
  - "Accounts/Tunnels/ConnectedServersClient.cs"
  - "Dashboard.Client/Pages/Households.razor"
  - "Dashboard.Client/State/Households/**"
  - "Dashboard.Client/Effects/HouseholdsEffect.cs"
  - "Dashboard.Client/Services/HouseholdsAdminService.cs"
  - "Tests/Integration/Accounts/HouseholdListAdminTests.cs"
  - "Tests/E2E/Dashboard/HouseholdListE2ETests.cs"
  - "Tests/E2E/Fixtures/DashboardAdminE2EFixture.cs"
  - "Accounts/Members/MemberStore.cs"
  - "WebChat.Client/State/Effects/HouseholdMembersEffect.cs"
  - "Tests/Integration/Accounts/HouseholdMembersAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdMembersE2ETests.cs"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
  - "Accounts/Admin/HouseholdSpacesAdmin.cs"
  - "Domain/DTOs/Household/SpaceListing.cs"
  - "WebChat.Client/State/Effects/HouseholdFeaturesEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdSpacesEffect.cs"
  - "Tests/Integration/Accounts/HouseholdFeaturesAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdSpacesAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdSpacesE2ETests.cs"
  - "Tests/Integration/Accounts/HouseholdSuspensionAdminTests.cs"
  - "Accounts/Tunnels/TunnelCredentialStore.cs"
  - "WebChat.Client/State/Effects/HouseholdServersEffect.cs"
  - "Tests/Integration/Accounts/HouseholdServersAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdServersE2ETests.cs"
  - "WebChat.Client/State/Effects/HouseholdUsageEffect.cs"
  - "Tests/Integration/Accounts/HouseholdUsageAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdUsageE2ETests.cs"
  - "Tests/E2E/Dashboard/DashboardHouseholdFilterE2ETests.cs"
  - "Dashboard.Client/Components/HouseholdPicker.razor"
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
`GET`/`PUT /household/features` (`FeaturesSection`) and `/household/spaces` (`AdminSpaces` read, `SpacesSection` saved; "Features and spaces"),
`GET /household/keys` (`HouseholdKeys`) and `PUT /household/keys/openrouter` (`OpenRouterKey`),
`/household/keys/idealista` (`IdealistaKeys`), `/household/keys/telegram/{agentId}` (`TelegramBotKey`), `PUT`/`DELETE /household/keys/lemonade` (`LemonadeBox`, the household's own Lemonade box: probed by its model listing with its key, `LemonadeBoxProbe`; another household's must be a public https address, `LemonadeAddresses`, which a test stack lifts with `KeyProbes:LemonadeOnPrivateAddresses`; shown as `HouseholdKeys.Lemonade`, its address and its key's state; or `LemonadeBox.Kit`, `{ throughKit: true }`, a box behind the household's router reached through its tenant kit's forwarder, held to neither rule and **saved unprobed** — there is no address to ask — recorded "Set the Lemonade box, through the tenant kit", and shown with `ThroughKit` and its forwarder's tunnel's `Connection` (`ServerConnection`, asked of the agent's relay through `ConnectedServersClient`; `Unknown` when it cannot be asked); one shape saved over the other replaces it. The page's Lemonade slot offers "At an address" or "Through the tenant kit", the latter asking nothing and saying what to set up in the kit) ("Keys and probes"),
`GET /household/changes?limit=` (the household's change log), the agents, members, own servers and usage routes (below), `GET /operator/changes?household=&limit=`
(every household's, or one's), and the household list's (below): `GET`/`POST /operator/households` and
`POST /operator/households/{householdId}/owners/{userId}/recovery`. A route is mapped in `AdminApi.Map` into one of the two groups, with its
path inside the group from `AdminApi.Under(root, AdminProtocol.X)`, and reads its caller as a bound
`AdminCaller` parameter (never null: the gate answered first). A route that takes an id has its template
there too (`AdminProtocol.XRoute`, `{agentId}` and the like) beside the builder the clients call
(`AdminProtocol.X(id)`), which escapes every id it puts in the path; a service never writes a template
by hand.

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
   receives an `admin` one (the proxy copies it onto the request it hands this service). That holds
   only because no host routes the session check itself: `/accounts/verify*` is `respond 404` wherever
   `/accounts/*` reaches this service ("The proxy"), or a same-origin script could ask it for an
   `admin` assertion with the cookie and read it out of the answer's headers.
3. **The root's role**: `owner` (`ForwardedAccount.OwnerRole`) for `/household`, `operator` for `/operator`,
   else **403** with an `AdminRefusal`.

Every admin answer is `Cache-Control: no-store`. The session's liveness is the session check's: it ran
on this very request (forward_auth), so the assertion is seconds old; a suspended household (below) and
disabled members (04) are refused there, not here.

## Answers

- A read answers **200** with its shape (`HouseholdSettings`, a list of `AdminChangeEntry`).
- A change that stood answers **200** `AdminSaved<T> { value, warning }`: what is saved now, read back,
  and a warning when it stood with a caveat — readers not told (`AdminSections.NotToldWarning`, the one
  wording for every section and the members' changes; keys, own servers and suspension say what is
  particular to them), or for 03 a key saved while its provider could not be asked. A section's save
  is answered through `AdminSections.AnswerAsync` (its rules' refusal 400, no household 404, the route's
  answer when it stood), and a summary's changed fields are `AdminLog.Changed`.
- A change refused answers **400** `AdminRefusal { message, problems }`, nothing changed and nothing
  recorded; the problems are the section rules' own words (`SectionSchema.Problems`), never a value sent.
- **401** signed out (the session check's own answer, or the gate's), **403** `AdminRefusal` not allowed,
  **404** no such route or household, **503** `AdminRefusal` what the deployment cannot do just now (keys
  with no master key, usage with no Observability host), read or save alike.

## Two owners with the same page open

A page that saves a whole section — settings, features, spaces — or an agent's name and instructions sends
what it loaded beside what it wants (`AdminEdit.Of`, the body's `loaded`; `AgentEdit.Loaded`), and the route
lays only what the page changed over the section as it is now (`AdminSections.Merged` over `EditMerge`): a
field the page left as it loaded it takes the stored value, so another owner's change since stands, and spaces
merge by slug, so one owner's added space survives another's save. A field both changed to different values —
or a space one removed and the other changed — is refused **409** `AdminRefusal` naming it ("space attic", "the
time zone", "its name"), nothing saved or recorded; the chat client shows it as a refusal (`AdminService`). A body
with nothing loaded (the operator's tools, a test, a client from before) is saved as sent. The user chose merging
per item over refusing every stale save (2026-11-04). The keys, members and servers routes change one item each
and need none of it. **A household's changes run one at a time** (`OneChangeAtATime`, a filter on the owners' group for
every method but GET/HEAD): each change reads its section, lays itself over it and saves it whole — a key's save asks
its provider in between — so two in flight at once saved over each other (Bruno's Idealista keys were lost to Ana's
OpenRouter save). A slow probe (at most `KeyProbes:Timeout`) delays another owner's save of the same household; the
operator's routes and the container's commands are not queued. **The spaces page keeps each space's loaded order**
(`SpaceDrafts`: a move swaps two, an added space goes last): numbering every space again made one owner's removal a
change to every space after it, and another owner's removal of a different space was refused 409.

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

## Keys and probes

The keys page (`KeysAdmin`, over 4b's `keys` section) saves each key on its own route, **after its
provider was asked** (`Accounts/Keys/KeyProbes.cs`):

- **One contract, one probe per provider**: `IKeyProbe<TKey>` answering `KeyProbeOutcome.Working`,
  `Rejected(why)` or `Unreachable(why)`. `HttpKeyProbe<TKey>` is the one-request kind: a 2xx works, the
  probe's `Refusals` (401/403; Idealista adds 400, OAuth's `invalid_client`; Telegram 404, a token it
  cannot read) reject, **anything else** — 5xx, 429, a timeout (`KeyProbes:Timeout`), no connection — is
  unreachable. OpenRouter: `GET {OpenRouterUrl}key` with the key as bearer (bills nothing). Idealista:
  its token exchange. Telegram: `getMe`, after the token is checked against the shape BotFather issues
  (it goes into a URL path). Adding a provider is one probe and one `KeySlot`.
- **The base URLs are settings** (`KeyProbes:OpenRouterUrl`, `IdealistaUrl`, `TelegramUrl`, `Timeout`
  in `Accounts/appsettings.json`, the real hosts by default), so a stack points them at a stub
  (`FakeKeyProviders`; the E2E stack's `KEYPROBES__*` at one in process through the host's gateway).
  The probes' HTTP client (`key-probes`) has its request loggers removed: a bot's token is in the URL.
- **A save**: the section's own rules first (a blank key is refused in their words, nobody asked), then
  the probe — **rejected refuses the save** (400, `AdminRefusal` "The X was not saved: <why>.", nothing
  saved or recorded); working or unreachable save through `HouseholdConfigStore.SaveAsync` (the section
  read whole, the one key put, saved whole: the others kept, every secret sealed again), then the check
  is stored and the change recorded ("Set the OpenRouter key" / "Replaced …", "the Idealista keys",
  "the Telegram bot for <agent>"). Unreachable answers `AdminSaved.Warning` ("could not be asked …: it
  shows as unchecked"), joined with the not-told warning when both apply. A bot must be bound to one of
  the household's agents (`HouseholdAgents.Names`, the two templates for a household that saved none).
  A stack with no master key answers a read or a save **503** `AdminRefusal` with
  `KeysAdmin.SecretsUnavailableMessage` — the deployment's to fix, not the request's — never a 500 and
  never a 400; the chat client shows the refusal's message (`AdminAnswer.Unreachable(message)`).
- **State lives beside the section, not in it**: `HouseholdKeyChecks` (table `HouseholdKeyChecks`, one
  row per household and key name — `openRouter`, `idealista`, `telegramBots/<agent>`): state, when
  checked (`AccountsClock`), a reason, and the key's **fingerprint** (`Tokens.Hash` of the key, Idealista's
  of its pair). Not in the section because a state change (a re-probe, a rejection in use) is not a
  save — every save is announced and drops every reader's cache, Telegram's bots included — and the
  operator's `get`/`set-household-config` and the seed route stay as 4b made them. A key whose
  fingerprint is not the stored check's (the operator's command replaced it, asking nobody) reads
  **unchecked** with no check time.
- **Never the key**: `HouseholdKeys { paysWithDeploymentKey, openRouter, idealista, telegramBots,
  needsOpenRouterKey, isHome }`, each a `KeyStatus { state, lastCharacters, checkedAt, reason }` —
  `AdminProtocol.KeyCharactersShown` (4) last characters, none for a key under `ShortestKeyShown` (12),
  Idealista's from its API key alone, never its secret. `needsOpenRouterKey` is an owner's household
  that is not the home and has no OpenRouter key. `paysWithDeploymentKey` is the home with no key of its
  own: one saved for it takes priority (`HouseholdAgents.Payer`), and the page says the deployment's runs
  only until one is set. `isHome` picks the Lemonade slot's wording (the home with no box runs on the
  deployment's, and its own may be on its network).
- **A key failing in a turn**: `RefusedKeyReportingChatClient` wraps the OpenRouter client of a turn
  sent on a household's own key (`MultiAgentFactory.CreateChatClient` with a key and its household; never
  the deployment's key, never the Lemonade box) and on a `ClientResultException` 401 — not a 402, which
  is credit — calls `IHouseholdKeyReports.OpenRouterKeyRefusedAsync(household)` once per client, without
  waiting. The turn then fails as a `TurnRefusedException` carrying `RefusalAnswers.KeyRefused` (a 402,
  `OutOfCredit`, is not reported but is refused the same way), which the reply shows bare — never the
  provider's status line — and voice speaks in Spanish (`SpokenRefusals`). `HouseholdConfigurationClient` implements it (registered only where an agent
  token is given): `POST /accounts/internal/household-config/openrouter-key-refused`, proven as the
  agent's own view is (`InternalCallers`' view table, `HouseholdProofs.AgentAsync`), answered 204.
  `KeysAdmin.InUseRejectedAsync` marks the key saved now rejected, then probes it: working heals it,
  rejected keeps it with the provider's why, unreachable leaves it rejected. An internal route rather
  than a Redis event: Redis messages carry no proof of who sent them, and accounts subscribes to nothing.
- **Onboarding**: the chat client's `OnboardingEffect`, on `SignedIn`, asks an owner's keys (outside the
  admin area only — the area signs in too) and, when `needsOpenRouterKey`, enters `AdminArea.Keys` by a
  full load. The keys page says why there (`role="note"`).

## Agents

`HouseholdAgentsAdmin` over the agents section, and `AgentVersions` beside it (`Domain/DTOs/Admin/AdminAgents.cs`):
`GET /household/agents` (`AdminAgents`: the default, each agent as `AdminAgent` — id, name, custom
instructions, the template it was made from or none for a seeded definition, description and model shown,
never edited — the shipped templates, and the instructions limit); `POST /household/agents`
(`AgentAddition { template, name }`); `PUT`/`DELETE /household/agents/{agentId}` (`AgentEdit { name,
customInstructions }`); `GET /household/agents/{agentId}/versions` (`AdminAgentVersion`, newest first) and
`POST .../versions/{versionId}/restore`; `PUT /household/voice-agent` (`VoiceAgentChoice { agentId }`, null for
none — beside the agents rather than under them, where it would read as an agent's id). Every change answers
`AdminSaved<AdminAgents>`, the whole list, with `voiceAgent` (the one marked) and `answersAloud` (the one that
does, `HouseholdAgents.VoiceAgentOf`, or null).

- **Which agent answers aloud is the owner's too**: any of the household's agents that holds a conversation, whatever its template (an agent with `no-tools`, the Sugary estimator, answers one request and is never offered: `HouseholdAgent.MayAnswerAloud`, `AdminAgent.MayAnswerAloud`, and the section's rule refuses the mark) —
  a Jonas answers a satellite under the voice rules as a Nabu does (`.claude/rules/channels.md`). Marked
  none, the household's first agent of the voice template answers. The section's own rule refuses a mark
  naming none of its agents ("Marked agent jonas to answer aloud" / "Marked no agent to answer aloud" in the
  log); removing the marked agent clears the mark in the same save. The page is a select under the agents,
  saying who answers now.
- **Only the name and the custom instructions are the owner's** of an agent itself. An edit carries those two and nothing
  else binds (a model sent beside them is ignored). They live where the section says: a template entry's
  `name`/`customInstructions`, a seeded definition's own `definition.name`/`definition.customInstructions`
  (the home's jack, jonas, nabu and sugary) — so the home's agents are renamed like any other.
- **Limits are the admin API's**, beyond the section's rules: a name 1–`AdminAgent.NameLength` (100)
  characters, instructions at most `Admin:AgentInstructionsLength` (appsettings, 4000) — they are in every
  turn's prompt. The operator's `set-household-config` is not held to them.
- **A household that saved no agents** reads as the two starting templates (`HouseholdAgents.Effective`),
  and its first change is made to those, so renaming jonas keeps sugary.
- **An added agent** takes the template's id, or the first free `<template>-<n>`. A template this build
  does not ship is refused.
- **Removing** the default agent is refused ("answers by default, so it stays: rename it…") — nothing
  else names which agent answers a message that names none — and so is removing the last agent, since a
  section with none reads as the two starting templates again. So is removing the agent a space opens on
  ("The Kitchen space opens on Chef, so it stays: pick another agent for that space first"), refused rather
  than cleared so the removal stays one save of one section. So is removing an agent that still runs
  schedules ("Chef runs 2 schedules, so it stays: ask it to delete them, or to move them to another agent,
  first"): they would stay and be refused at every fire with nobody told. `HouseholdSchedules` counts them
  where the scheduler keeps them (`ScheduleKeys`, the deployment's Redis, through `AccountsRedis`, which
  `RedisAnnouncer` shares); a Redis that cannot be read refuses the removal 503, and no Redis configured
  counts none. So is removing an agent a household Telegram
  bot answers as ("The household's Telegram bot answers as Chef, so it stays: bind the bot to another
  agent first"; user's decision, 2026-11-05) — a bot's token saved under another agent on the Keys page
  moves there (`KeySlot.TelegramBot` drops the token's other binding: one bot is never polled twice).
  **A removed agent's id is never given to another**
  (`AgentsSection.RemovedIds`, appended by the removal and skipped by `FreeId`): an id keys its people's
  conversations, its schedules and a bot bound to it, and none of that goes with the agent, so the next
  agent of the template under the same id inherited all of it.
  **Its people's conversations with it are no longer shown** (kept under its id, listed nowhere; user's
  decision, 2026-11-05: say so rather than refuse): the confirmation says so, and a chat page that was on it
  when it went names it above the agent it fell back to (`TopicsState.RemovedAgentName`, until the person
  picks an agent or opens a conversation) — before, Marta's open Chef conversation closed under her and her
  next message went to Jonas with nothing said.
- **Versions** are the name and instructions a change replaced (an edit's or a restore's, so a restore is
  undone the same way), the last `Admin:AgentVersionsKept` (5) per agent, by whom and when, written after
  the save stood and dropped with the agent. **A table, not the section**: no turn reads history, and the
  section is what the agent is sent on every view, migrated by every step and rewritten whole by the
  operator's command. That command writes no version.
- Summaries name the agent's id and the fields changed ("Changed agent jonas's name, custom
  instructions", "Added agent jonas-2 from the jonas template", "Restored a version of agent jonas",
  "Removed agent jonas-2"), never a name or instructions.
- **The page** is `/admin/agents` (`HouseholdAgentsPage`, `HouseholdAgentsStore`, `HouseholdAgentsEffect`):
  one article per agent named by its saved name, its fields, its earlier versions on asking (each with
  Restore), Remove behind a confirmation (not offered for the default or the last agent), and a form
  adding one from a template. The E2E test adds its own agent to the home and removes it, never touching
  the home's seeded ones.

## Members

`HouseholdMembersAdmin` over the accounts service's stores (`MemberStore`, `LinkStore`), never a copy
of them (`Domain/DTOs/Admin/AdminMembers.cs`): `GET /household/members` (`AdminMembers`: each
`AdminMember` — id, display name, role `owner`/`member`, operator, sees-all, disabled, reads-Sugary-logs); `POST
/household/members/invites` (`MemberInvite { displayName, role }`) and `POST .../{userId}/recovery`, each
answering `AdminSaved<MemberLink>` — the link once, never again (only its hash is kept), an invite's also
as the bot's deep link (`TelegramLink`) or, with no bot named, `/start <token>` (`TelegramStart`), which
the bot redeems in place of a passkey; `PUT .../{userId}` (`MemberEdit { role?, seesAll?, readsSugaryLogs? }`, a field left
out left as it is; `readsSugaryLogs` lets the member's agent read everyone's Sugary log, `.claude/rules/sugary.md`) and `POST .../{userId}/disable` / `.../enable`, each answering
`AdminSaved<AdminMembers>`, the whole list. A body-less POST is fine: the path says whom.

A Telegram group is bound from here too: `POST /household/members/telegram-groups` (`AdminProtocol.MemberTelegramGroups`) mints a group code, answered once as `TelegramGroupCode { bind: "/bind <code>", expiresAt }` and recorded as "Minted a Telegram group code", never its characters. This is the household's one members page; the accounts service's `/accounts/members` redirects here.

- **The last enabled owner** is neither demoted nor disabled (400): both are checked inside the one
  `UPDATE` (`MemberStore.ChangeRoleAsync`, `DisableAsync`), counting only enabled owners — a disabled
  owner manages nothing, so demoting the last enabled one beside a disabled one is refused too.
- **The operator** is never disabled nor minted a recovery link by an owner (403): their account is the
  deployment's way onto its dashboard and back in, which the container's command keeps.
- **A disabled member** is minted no recovery link (400: it would open nothing until re-enabled) — by
  any way one is minted, since `LinkStore.MintRecoveryAsync` itself answers a `RecoveryMint` (`Minted`,
  `Nobody` or `Disabled`) for every recovery it is named a person for: this route, the old members page
  (400 with a notice), the operator's owner recovery (400) and `mint-link --user` (exit 1, "is disabled")
  each say why rather than hand out a dead link.
- **Disabling** (`MemberStore.DisableAsync`, one transaction) sets `User.DisabledAt` and revokes every
  session the member holds; nothing else of theirs is touched. Every store that vouches for a person reads
  the column (`.claude/rules/accounts.md`, "Disabled members"), and the change is announced on
  `HouseholdConfigProtocol.SavedChannel` as section `PeopleChanged` so the agent drops the view that
  lists who is disabled (`AgentHouseholdConfig.DisabledPeople`) and refuses their turns and fires from
  their next one (`.claude/rules/service-identity.md`). Re-enabling clears the column and is announced
  the same way; their sessions stay ended — they sign in again with a passkey they kept or a recovery
  link — and their Telegram, voice profile, passkeys, links still in date, memories and topics answer
  for them again. Not told, the change stands with `AdminSections.NotToldWarning` (the agent
  sees it within `Accounts:ConfigurationLifetime`).
- **Role and sees-all reach the member's next turn**: the chat hub asks a fresh assertion for every turn
  (`SessionRefresh`), signed with the roles as they are then, and so does every voice utterance. Two
  things keep the old ones a while: the hub's own topic view (`TopicViewer`, its sees-all) is the
  connection's, so the change is announced (`PeopleChanged`) and each open page renews its connection
  (`OnAccessChanged`): a grant shows the topics at once, a withdrawal takes them away and closes one it had
  open (`ReconnectionEffect`); and
  Telegram keeps the assertion that admitted a sender for `LinkCache:Linked` (5 minutes).
- Summaries name the member's id and what changed, never a name or any part of a link: "Invited somebody
  as a member" / "as an owner", "Changed member <id>'s role, sees all conversations, reads everyone's Sugary log", "Disabled member
  <id>", "Re-enabled member <id>", "Minted a recovery link for member <id>".
- **The page** is `/admin/members` (`HouseholdMembersPage`, `HouseholdMembersStore`,
  `HouseholdMembersEffect`): one article per person named by their display name, with a role select,
  a sees-all and a "Reads everyone's Sugary log" checkbox saved together, Recovery link (not for the operator or the disabled), Disable
  behind a confirmation (not offered for the operator) or Re-enable, and the invite form. A minted link
  is shown in a `New link` region until the next change or a reload, never again. The E2E test makes its
  own people (`create-user`) and changes only them.

## Features and spaces

- **Features** (`HouseholdFeaturesAdmin`): `GET`/`PUT /household/features` reads and saves the features
  section whole (a `JsonObject`, as the settings route takes, so an unknown feature is refused in the
  section's own words), answered `AdminSaved<FeaturesSection>`, recorded as "Changed the scheduling, memory
  features" — names, never on or off. The save is announced, so the household's next turn is built
  without what was turned off (`HouseholdAgents.Offered`, `.claude/rules/household-config.md`); the turn
  side is proven at `HouseholdAgentSource` over the agent's own reader (`HouseholdFeaturesAdminTests`).
- **Spaces** (`HouseholdSpacesAdmin`): `GET /household/spaces` answers `AdminSpaces { spaces, agents }` — the
  spaces in their order (`SpaceListing.Ordered`, the hub's) and the household's agents a space may open on
  (`HouseholdAgents.Names`: the ones its turns run, under their names), so the page needs one load. `PUT`
  takes the spaces section whole (`SpacesSection`, names trimmed), answered `AdminSaved<AdminSpaces>`.
  Beyond the section's rules: **the default space** (`default`) is never removed while the household has it
  — a removed space's topics are listed there, alarms push there and the chat's own address opens it —
  only renamed, recoloured and reordered; a name is at most `AdminSpaces.NameLength` (100); a space opens on
  one of the household's agents or on none. A slug is set when a space is added and never edited: a new
  slug is a new space, and the old one's topics go to the default space. Recorded per space by slug:
  "Added space kitchen; removed space attic; changed space default's name, colour, default agent, order".
- **A removed space's topics are not moved** (`.claude/rules/channels.md`, "Spaces"): they stay under
  their slug and the default space lists, searches and hears them, read side, so a removal is one save
  and a space added back under its slug has its conversations again.
- **A removed space's phones** (`push:space@{tenant}:{slug}`) are left as they are: nothing pushes to a
  space nobody can join (a reply's push goes to the space its session joined), the set is never read
  again, and each phone subscribes in whatever space its app opens next — a removed slug opens the
  household's first — while unsubscribing still clears it from every space's set.
- **The pages** are `/admin/features` (`HouseholdFeaturesPage`: one checkbox per feature, saved together)
  and `/admin/spaces` (`HouseholdSpacesPage`: one article per space with its name, colour, the agent it
  opens on, Move up / Move down and Remove — not offered for the default space — and a form adding one by
  its address and name; every change is the page's until "Save spaces" sends the list in its new order).
  The E2E test (`HouseholdSpacesE2ETests`) adds a tagged space to the home, finds it in the chat with its
  name, colour and agent, edits and removes it; it opens the features page and never saves it.

## Own servers

`HouseholdServersAdmin` over 4a's tunnel credentials (`TunnelCredentialStore`, `.claude/rules/accounts.md`)
and the agent's relay (`ConnectedServersClient`, the household list's client) (`Domain/DTOs/Admin/AdminServers.cs`):
`GET /household/servers` (`AdminServers`: each `AdminServer` — name, outpost, `ServerConnection`
`Connected`/`Offline`/`Unknown`, connected since, tools, and its live credentials as `AdminServerCredential`
— id, when minted, in use); `POST /household/servers` (`ServerAddition { name }`), answering
`AdminSaved<MintedServerCredential>` — the credential once, never again (only its hash is kept); `POST
/household/servers/credentials/{credentialId}/revoke`, answering `AdminSaved<AdminServers>`.

- **The list** is every server name holding a live credential of the household, joined by name with the
  relay's listing — and a name with a tunnel open but no live credential (revoked while the relay could not
  be told), which is still connected and shown so. The relay not answering (`ConnectedServersClient` null)
  is every server `Unknown`, never offline; answered without it, `Offline`. Connected carries the tunnel's
  opening time and the tools it listed through its tunnel just now (null: did not answer), and marks the
  credential the tunnel was opened with as in use — the relay's listing carries `ConnectedServer.CredentialId`
  for that — so an owner replacing a credential (mint another, restart the server with it, revoke the old)
  sees which one to keep.
- **A name** is held to `TunnelCredentialProtocol.IsServerName` (a DNS label: it goes into the server's
  synthetic address) before minting, refused 400 in those words with nothing minted. Minting for a name
  already listed adds a second credential; nothing is replaced.
- **`lemonade` is minted here too**, for a household whose Lemonade box is reached through its tenant kit
  (the Keys page says so): listed with its state like any server, and never a tool server — no agent dials
  it (`.claude/rules/household-config.md`). Revoking it closes the forwarder's tunnel, and the box offers no
  models from the agent's next refresh.
- **Outposts are minted here too**: a machine's credential is one for `outpost-<NAME>`
  (`OutpostServerName`), and the list marks it by that name, as the relay tells a machine apart — the
  verified name is what makes an outpost, not who minted it, and a household's machines are its owners'.
- **Revoking is the caller's household's alone**: `TunnelCredentialStore.RevokeAsync(id, tenantId)` matches
  the household and the id together (404 otherwise, nothing revoked or recorded) — `revoke-tunnel-credential`
  names an id alone, and an id is no secret. It publishes the revocation as the command does, so the relay
  closes the tunnel at once and refuses its reopen. Told, the answer leaves out the tunnel opened with that
  credential without waiting for the close to land (the relay takes it out of its registry first thing);
  not told, the revocation stands with `HouseholdServersAdmin.NotToldWarning` — the server stays connected
  until its tunnel next reopens, and is refused then.
- Summaries name the server, never the credential: "Minted a tunnel credential for server <name>",
  "Revoked a tunnel credential of server <name>".
- **The page** is `/admin/servers` (`HouseholdServersPage`, `HouseholdServersStore`, `HouseholdServersEffect`):
  one article per server named by its name, its state and tools, each credential with Revoke behind a
  confirmation, New credential, and the add form. A minted credential is shown in a `New credential` region
  until the next change or a reload, never again; the effect asks the list again after a mint.

## Usage

`GET /household/usage?month=yyyy-MM` (`AdminProtocol.Usage`, `HouseholdUsageAdmin`) answers `AdminUsage
{ month, agents, people, lemonade }`: the caller's household's tokens and cost per agent and per person
(`UsageLine { name, tokens, cost }`, costliest first) and its Lemonade use (`LemonadeUsage { transcribedSeconds,
spokenCharacters, embeddings }`). A month is its UTC days, first to last (the metrics are kept by UTC day);
none named is the current one on `AccountsClock`; anything but `yyyy-MM` is 400. A read: nothing is recorded.

- **The figures are the Observability host's**, which this service otherwise never reads: `HouseholdUsageClient`
  asks its usage route (`HouseholdUsage.Path`, `/api/usage?from=&to=`) at `AccountsSettings.ObservabilityUrl`
  (`OBSERVABILITYURL` in compose, `http://observability:8080` by default) within `UsageTimeout` (appsettings,
  10 s). The host could not be asked — down, refusing, slow — is **503** `AdminRefusal`
  (`HouseholdUsageAdmin.UnavailableMessage`), which the page shows as "could not be read", never as a month
  with nothing used.
- **The credential is an assertion this service signs** (`Assertions.Of(ForwardedAccount, channel)`) for the
  owner the gate verified, re-signed for **`AppChannels.Usage`** (`usage`), a channel no proxy block names, so
  no browser is ever handed one and the admin assertion — the accounts service's alone — is never passed on.
  The host already verifies this service's keys (`OperatorGate`), so no secret exists for it.
- **The household is the assertion's**: the host's gate admits that one path for a `usage` assertion with the
  `owner` role (401 for none or another channel's, 403 for a member's), keeps the owner for the route
  (`UsageOwner`), and the route reads no `?household=`; every other path stays the operator's on the
  dashboard's channel, so a usage assertion opens none of them (`.claude/rules/observability.md`).
- **Per person is by account** (`TokenUsageEvent.UserId`, the account a call was billed to), so a rename or a
  memory call naming only the id does not split a person; the Observability host labels each with the latest
  name their calls carried (`MetricsQueryService.PersonLabels`) and answers the id beside it (`UsageLine.Id`),
  and this route replaces that with the member's display name now (`MemberStore.ListAsync`), so a rename shows
  over the whole month. A call with no account (from before accounts were carried, the guest) is a line under
  its `Sender` with no id. Per agent is by id under its latest name, as the dashboard labels agents
  (`MetricsQueryService.AgentLabels`).
- **History is a month deep**: daily roll-ups expire thirty days after they were written, so an earlier month
  shows only its days still kept; the page says so.
- **The page** is `/admin/usage` (`HouseholdUsagePage`, `HouseholdUsageStore`, `HouseholdUsageEffect`): a month
  input, a "Per agent" and a "Per person" table (`Components/Admin/UsageTable`), and a "Speech and memory"
  region. Figures that could not be read are cleared, never left on show under another month.

The operator reads every household's figures on the dashboard's own pages, through its household filter
(`HouseholdPicker`, `.claude/rules/observability.md`), not through this route.

## The proxy

`DockerCompose/caddy/Caddyfile`'s main host routes `handle /accounts/admin/*` to `accounts:8080` behind
`forward_auth` with `uri /accounts/verify?channel=admin` and the four `copy_headers`, **after** the
`/accounts/internal/*` refusal and **before** `/accounts/*` (which is unchecked). The session check takes
any non-empty channel, so `admin` needed no list. The other hosts that route `/accounts/*` (SexyTime's,
Sugary's) answer `/accounts/admin/*` with `respond 404`: nothing there calls it, and their `/accounts/*`
would otherwise hand it on unchecked. `DeploymentConformanceTests.TheAdminApi_IsReachedThroughItsSessionCheckAlone`
holds every host to either the checked route or no route to the accounts service; the E2E stack's own
Caddyfile (`WebChatStack`) mirrors the main host's. The **dashboard's host** routes the operator's routes
alone, `handle /accounts/admin/operator/*`, with a `forward_auth` for `channel=admin` inside it (the
host-level `role=operator&channel=dashboard` check runs first; the inner one's `copy_headers` sets the
admin assertion over the dashboard's), proxied to `accounts:8080`, after its own `/accounts/internal/*`
refusal; the owners' routes there fall to the dashboard and are refused. `DashboardAdminE2EFixture`
mirrors that host.

**The session check is never routed.** Every host that routes `/accounts/*` to this service (main,
SexyTime's, Sugary's) answers `handle /accounts/verify*` with `respond 404`, beside and after its
`/accounts/internal/*` refusal: through the unchecked `/accounts/*` a same-origin script could ask
`/accounts/verify?channel=admin` (or any channel) with the cookie and read a ten-minute assertion out of
the answer's headers. `forward_auth` asks `accounts:8080` on the compose network and never comes this
way; nothing in the clients calls it. The dashboard's host routes no `/accounts/*`, so the path falls to
the dashboard there. `DeploymentConformanceTests.TheSessionCheck_IsAnswered404OnEveryHostThatRoutesTheAccountsService`;
`WebChatStack` mirrors the refusal.

## The household list (dashboard)

The operator's households, from the dashboard (`HouseholdListAdmin`, `/households` in `Dashboard.Client`):

- **`GET /operator/households`** answers every household as a `HouseholdListEntry`, the home first: its
  owners and members (`HouseholdPerson`, the operator flagged), its **connected servers** and its
  **last seen**. The servers are the agent's relay's, which lives in the agent's serving process alone:
  `ConnectedServersClient` asks the agent's own listing (`GET {AgentUrl}/internal/tunnels/{householdId}`,
  `ConnectedServers.Path`, the one `list-household-servers` reads) with `CALLERS__agent` as its bearer —
  the credential the agent presents here, which compose feeds the agent API's own secret, so no secret
  exists for this (`DeploymentConformanceTests.TheAccountsService_AsksTheAgentsRelayWithTheAgentApisSecret`).
  An agent not configured, down, refusing or slower than `AgentListingTimeout` (appsettings, 15 s: the
  relay asks each server for its tools for up to ten) leaves that household's servers **null**, shown as
  unknown, never a failed list. Owners' pages (07) read the same client (`HouseholdServersAdmin`, "Own servers"). **Last seen** is the latest
  `Session.LastSeenAt` of any of the household's sessions — the session check's own record, at its
  five-minute grain — so a person who only uses Telegram or voice never moves it; what a household spends
  is the usage view's (08), read from the metrics, which this service never reads.
- **`POST /operator/households`** `NewHousehold { name, ownerName }` creates the household (`Tenant`), its
  one owner (a `User` with no passkey yet) and the owner's `LinkKind.Recovery` link in one `SaveChanges`
  (`LinkStore.CreateHouseholdAsync`), answered `AdminSaved<OwnerLink>`; the name is held to the settings'
  length and the owner's to `Profile.Read`, refused 400 in those words with nothing written. A recovery
  rather than an invite, so the owner is listed (and recoverable) before the link is used. **Nothing of
  its configuration is written**: a section it never saved reads as its defaults — its name from the
  tenant row, the one default space, the default features — and a section naming no agent is the two
  templates (`HouseholdAgents.Starting`), so its first turn has agents.
- **`POST /operator/households/{householdId}/owners/{userId}/recovery`** mints a recovery link for an
  owner of any household, answered `AdminSaved<OwnerLink>`: 404 for nobody of that id in that household,
  400 for a member (their owners mint theirs), the operator (their way back is their account page or the
  container command: a stolen dashboard session must not become a passkey) or an owner another owner
  disabled (`RecoveryMint.Disabled`: the link would open nothing until they are re-enabled).
- Both mints are recorded on the household they are for, the link never in the summary. A link is in
  the answer alone; the page holds it in its state until "Done" or the page is left, and never asks again.
- **Suspension** (`HouseholdSuspensionAdmin`): **`PUT /operator/households/{householdId}/suspension`**
  `HouseholdSuspension { suspended }` sets `Tenant.SuspendedAt` (kept when already suspended, cleared on
  resume), answered `AdminSaved<HouseholdSuspension { suspended, since }>`; the home is 400 (the
  operator's own: nothing would be left to resume it from), no such household 404. Recorded as
  "Suspended the household" / "Resumed the household" only when it changed; it is announced every time
  (asking again is the retry): `HouseholdConfigSaved(id, HouseholdConfigProtocol.SuspensionChanged)` so
  every reader of the household's configuration drops what it kept, and on suspending
  `HouseholdSuspended(id)` on `TunnelCredentialProtocol.SuspendedChannel` so the relay closes its open
  tunnels; not told stands with `NotToldWarning`. **Nothing is deleted.** Each place that lets a
  household in reads the row:
  - the session check, and the account and members pages, which read their own cookie (`PageGuard`
    answers `SessionCheck.Suspended` before anything else, so no invite, recovery link, group code,
    passkey or profile change), `.claude/rules/accounts.md`;
  - the channels' ways in — `resolve`, the guest, the Telegram `start` (nothing spent) and `web-link` —
    answer `SuspendedHouseholdAnswer` (423), neither a person nor nobody, so no channel vouches for its
    people and the bot tells them why (`TelegramGate.SuspendedAnswer`);
  - the agent's view (`AgentHouseholdConfig.Suspended`), refused per message in `ConversationGroup` —
    every turn, and every fire whether delegated or not, by the household its server keeps the object
    under (`ChannelMessage.HouseholdId`, a schedule's `TenantId`) as well as its person's
    (`.claude/rules/channels.md`);
  - dreaming, which skips the household (`MemoryDreamingService` reads `Suspended` off the same view it
    reads the key from), and memory extraction, which asks nothing for a turn admitted before the
    suspension (`MemoryExtractionWorker`, the same view; a member disabled since their turn likewise);
  - the tunnel verify (`TunnelCredentialRefusal.Suspended`) and a household server's back-call
    (`TunnelCredentialStore.LiveAsync` answers none).

  Each reads the column in a query of its own shape, so nothing gathers them but a list:
  `SuspensionCoverageTests` places every route the accounts service maps, by path, with how it refuses
  a suspended household or why it need not, and fails on a route it does not place — a new way in
  says what suspension does to it before it ships.

  The list's `HouseholdListEntry.SuspendedAt` and the row's Status cell (Suspend / Resume, none for the
  home) are the dashboard's.
- **Its open pages are told at once**: the chat hub hears `SuspensionChanged` as it hears the people
  changing (`HouseholdAgentsAnnouncer`) and sends the household's pages `OnAccessChanged`, so each opens
  a fresh connection, which the session check refuses as suspended and the page shows as the household's
  notice — before, a page stayed "Connected" until its person reloaded or sent something. **And back again once resumed**: the chat client goes to the notice as `/accounts/account?returnUrl=<where it was>`, and the notice (`SessionCheck.Suspended`, `data-suspended`, with `data-return` only for a return address `ReturnUrls` accepts) asks for itself again every 20 s (`PasskeyScript`) and goes back there once it is no longer 403 — before, a resumed household's pages said "suspended" until a reload, which kept them on the account page.
- **Not stopped by it, deliberately**: the chat hub's per-turn session refresh still answers, so an open
  chat client's turn reaches the agent, which answers `SuspendedAnswer` — a refused refresh would end the
  stream "signed out", the wrong words. `/accounts/me` still names the person. A refused fire has been
  emitted all the same, so the scheduler advances a cron and deletes a one-shot as if it ran: a one-shot
  due while its household is suspended is gone when it is resumed (kept so by the user, 2026-11-05), and
  the dashboard's Suspend confirmation and the members page (for a disabled member's) say so.

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
  (`Answered(value, warning)`, `Refused(refusal)`, `SignedOut`, `NotAllowed(message)`, `Unreachable(message?)`,
  the message a 503's refusal). A new route is a method here, and an effect settles its answer with
  `AdminAnswers.Settle` (`State/Admin/`), naming only its page's outcome and not-saved action. Stores, effects and the service are registered by `AddWebChatAdmin()`,
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
back — and leaves turn-visible effects to seam 1. `HouseholdKeysE2ETests` so submits only a key the
stub rejects, and shows the landing by answering the page's keys read as a keyless household's would
be answered (`page.RouteAsync`); the E2E stack has no master key, so nothing there can save a key.
Seam 1 for keys is `KeysAdminTests` against `FakeKeyProviders`; the turn side of a refused key is
`MultiAgentFactoryHouseholdTests` (`FakeOpenRouter.Refuse`).

Own servers' seam 1 is `HouseholdServersAdminTests`, against the real relay (`RelayHost` and
`TunnelledServer` from `Tests/Integration/Tunnels`, the relay's port taken first since accounts is told the
relay's address and the relay the accounts service's): a server added through the admin route connects with
the credential shown, is listed with its tools and then offline, and a credential revoked there closes its
tunnel and is refused on the reopen. Seam 2 is `HouseholdServersE2ETests`, whose server is its own tagged name
in the home, which no agent names: listed offline (the stack's real agent is asked) and revoked.

Members' seam 1 is `HouseholdMembersAdminTests`, which checks a disabled member is nobody on every way in
(the session check, the session refresh, `resolve` for voice and Telegram, `web-link`, a recovery link,
`mint-session`) and that the agent's own reader says so (`HouseholdAccountStanding` over
`HouseholdConfigurationClient` and a real Redis); seam 4 is `ChatMonitorAssertionTests` and
`ChatMonitorDelegationTests` with a `FakeAccountStanding`; seam 2 is `HouseholdMembersE2ETests`.

The household list's seam 1 is `HouseholdListAdminTests`: `AccountsServer.Admin(session).PostAsync`, a
person of another household by `SeedPersonAsync(household, name, role)`, and the agent's listing as a
Kestrel stub behind the shared secret (`AccountsSettings.AgentUrl` at it, `Callers["agent"]` the
secret). The owner's link enrolling a passkey into the new household is a ceremony
(`PasskeyCeremoniesTests`). Seam 3 is `HouseholdListE2ETests` on `DashboardAdminE2EFixture` (collection
`DashboardAdminE2E`): accounts, observability and a Caddy mirroring the dashboard's host, the operator
signed in by `mint-link --operator` and `mint-session` (`CreateOperatorPageAsync`, which also answers
the home's id), anybody else by `CreatePersonPageAsync(name, role)`, and the relay's listing a stub
whose servers a test sets with `Connect(householdId, server)`. Its stack is its own, so a test may
create, change and suspend households freely; the fixture is the one 08 and 09 extend. Its accounts
service announces on the fixture's Redis; `OwnerSessionAsync(operatorPage, householdName)` mints a session
for the owner of a household the operator created (found through the operator's page's own list), and
`OpenAsAsync(session, path)` asks the host as that person's browser would over HTTP — the base fixture keeps
one page open at a time, so a second person cannot have a page beside the operator's.

Usage's seam 1 is `HouseholdUsageAdminTests`: the accounts service and the Observability host's shipped gate and
API in process (`AddOperatorGate` over the accounts server's own JWKS, `MetricsQueryService` on a `RedisFixture`,
both on the accounts clock, the host's port taken first since accounts is told it), roll-ups written by
`MetricsCollectorService.ProcessEventAsync` for two households: each owner reads their own and never the other's,
a `?household=` naming another is ignored, the route refuses every assertion but an owner's usage one, and a
host nobody answers on is 503. Seam 2 is `HouseholdUsageE2ETests`: the E2E stack runs no Observability host, so
the real read is the unavailable notice, and a month's figures are shown by answering the page's read
(`page.RouteAsync`). Seam 3 is `DashboardHouseholdFilterE2ETests` on `DashboardAdminE2EFixture`, which publishes
events on its Redis (`RedisConnectionString`, `metrics:events`).

Suspension's seam 1 is `HouseholdSuspensionAdminTests` (the route, the home refused, the announcements on a
`RedisFixture`, the tunnel verify and back-call, the agent's reader seeing it and resuming) beside
`SessionCheckTests` (the notice and the 403); seam 4 is `ChatMonitorAssertionTests` and
`ChatMonitorDelegationTests` over `MonitorTestMocks.Households(suspended, from)` — a household suspended
from its n-th asking, so a conversation already running is refused from its next message — and
`TunnelRelayTests` (its tunnels closed `Suspended`, another household's left open).

Seam 5 is `StackSmokeTests.AHouseholdTheOperatorCreates_IsAnsweredByTheAgentItsOwnerNamed_UntilItIsSuspended` on
the smoke stack (`scripts/smoke-stack.sh`): the operator's create and suspend through the dashboard's host,
the owner signed in by `mint-session --user` from the create answer's `UserId`, the OpenRouter key and the
agent's name through the main host's household routes, every call with the admin header. The stack's
accounts service has a master key made for its smoke state and its `KEYPROBES__*` at `smoke-keys`, a stub
answering 200 to everything (`DockerCompose/docker-compose.override.smoke.yml`), so the key set is the
deployment's own, read from the agent's user secrets, and only the turn reaches OpenRouter. Each run makes
its own household, suspends it (the open page goes to the notice) and resumes it (the notice goes back to the
chat by itself); `down` removes it.
