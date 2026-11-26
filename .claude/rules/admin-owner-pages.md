---
paths:
  - "Accounts/Admin/HouseholdAgentsAdmin.cs"
  - "Accounts/Admin/AgentVersions.cs"
  - "Accounts/Admin/HouseholdSchedules.cs"
  - "Accounts/Admin/HouseholdMembersAdmin.cs"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
  - "Accounts/Admin/HouseholdSpacesAdmin.cs"
  - "Accounts/Admin/HouseholdAppAsksAdmin.cs"
  - "Domain/DTOs/Admin/AdminAppAsks.cs"
  - "WebChat.Client/Pages/Admin/HouseholdAppsPage.razor"
  - "WebChat.Client/State/Admin/HouseholdAppAsksStore.cs"
  - "WebChat.Client/State/Admin/AppCapDrafts.cs"
  - "WebChat.Client/State/Effects/HouseholdAppAsksEffect.cs"
  - "Tests/Integration/Accounts/HouseholdAppAsksAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdAppsE2ETests.cs"
  - "Accounts/Admin/HouseholdServersAdmin.cs"
  - "Accounts/Admin/HouseholdUsageAdmin.cs"
  - "Accounts/Admin/HouseholdUsageClient.cs"
  - "Accounts/Admin/HouseholdAppsClient.cs"
  - "WebChat.Client/Components/Admin/AppSharedFacts.razor"
  - "Accounts/Members/MemberStore.cs"
  - "Accounts/Tunnels/TunnelCredentialStore.cs"
  - "Accounts/Tunnels/ConnectedServersClient.cs"
  - "Domain/DTOs/Admin/AdminAgents.cs"
  - "Domain/DTOs/Admin/AdminMembers.cs"
  - "Domain/DTOs/Admin/AdminSpaces.cs"
  - "Domain/DTOs/Admin/AdminServers.cs"
  - "Domain/DTOs/Admin/AdminUsage.cs"
  - "Domain/DTOs/Household/SpaceListing.cs"
  - "WebChat.Client/Pages/Admin/HouseholdAgentsPage.razor"
  - "WebChat.Client/Pages/Admin/HouseholdMembersPage.razor"
  - "WebChat.Client/Pages/Admin/HouseholdFeaturesPage.razor"
  - "WebChat.Client/Pages/Admin/HouseholdSpacesPage.razor"
  - "WebChat.Client/Pages/Admin/HouseholdServersPage.razor"
  - "WebChat.Client/Pages/Admin/HouseholdUsagePage.razor"
  - "WebChat.Client/State/Admin/HouseholdAgentsStore.cs"
  - "WebChat.Client/State/Admin/HouseholdMembersStore.cs"
  - "WebChat.Client/State/Admin/HouseholdFeaturesStore.cs"
  - "WebChat.Client/State/Admin/HouseholdSpacesStore.cs"
  - "WebChat.Client/State/Admin/HouseholdServersStore.cs"
  - "WebChat.Client/State/Admin/HouseholdUsageStore.cs"
  - "WebChat.Client/State/Admin/SpaceDrafts.cs"
  - "WebChat.Client/State/Effects/HouseholdAgentsEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdMembersEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdFeaturesEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdSpacesEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdServersEffect.cs"
  - "WebChat.Client/State/Effects/HouseholdUsageEffect.cs"
  - "WebChat.Client/Components/Admin/AgentDefinitionText.cs"
  - "WebChat.Client/Components/Admin/MemberDrafts.cs"
  - "WebChat.Client/Components/Admin/FeatureHints.cs"
  - "WebChat.Client/Components/Admin/ServerCredentialPlace.cs"
  - "WebChat.Client/Components/Admin/UsageTable.razor"
  - "Tests/Integration/Accounts/HouseholdAgentsAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdMembersAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdFeaturesAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdSpacesAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdServersAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdUsageAdminTests.cs"
  - "Tests/E2E/WebChat/HouseholdAdminE2ETests.cs"
  - "Tests/E2E/WebChat/HouseholdMembersE2ETests.cs"
  - "Tests/E2E/WebChat/HouseholdSpacesE2ETests.cs"
  - "Tests/E2E/WebChat/HouseholdServersE2ETests.cs"
  - "Tests/E2E/WebChat/HouseholdUsageE2ETests.cs"
  - "Tests/E2E/Dashboard/DashboardHouseholdFilterE2ETests.cs"
---

# The admin API: the owners' pages

See also: `admin.md` (routes, gate, answers, merging stale saves, change log, the admin area),
`admin-keys.md`, `admin-operator.md`.

Route paths are spelled once in `AdminProtocol` (`Domain/DTOs/Admin/`), shared by the service and both
clients; request and answer shapes are the DTOs beside it. Verbs: `GET` reads; `POST` adds, mints, restores,
disables, enables and revokes; `PUT` edits an agent, a member, the voice choice, features, spaces or app caps;
`DELETE` removes an agent. Every summary these routes record names ids,
server names and which fields changed — never a person's or agent's name, instructions, a link, a
credential or a value.

## Agents

`HouseholdAgentsAdmin`, `AgentVersions` (`Domain/DTOs/Admin/AdminAgents.cs`): list, add (`AgentAddition`),
edit (`AgentEdit`), remove, versions and restore, and the voice choice (`VoiceAgentChoice`, null for none).
Every change answers `AdminSaved<AdminAgents>`, the whole list with `voiceAgent` and `answersAloud`
(`HouseholdAgents.VoiceAgentOf`).

- **The agent's `definition` is shown read only** (`HouseholdAgents.DefinitionOf`, before the deployment's
  servers are weighed, rendered by `AgentDefinitionText`), never a bot token (user, 2026-11-13).
- **Only the name and the custom instructions are the owner's**; nothing else in an edit binds (a model sent
  beside them is ignored). They live in a template entry's `name`/`customInstructions` or a seeded
  definition's `definition.name`/`definition.customInstructions`, so the home's agents are renamed like any
  other.
- **Limits are the admin API's**: name 1–`AdminAgent.NameLength`, instructions at most
  `Admin:AgentInstructionsLength` (they are in every turn's prompt). The operator's `set-household-config` is
  not held to them.
- **Which agent answers aloud is the owner's too**: any agent that holds a conversation, whatever its
  template; a `no-tools` agent (the Sugary estimator) is never offered (`HouseholdAgent.MayAnswerAloud`,
  `AdminAgent.MayAnswerAloud`; the section's rule refuses the mark, and a mark naming none of its agents).
  A Jonas answers a satellite under the voice rules as a Nabu does (`.claude/rules/channels.md`). Marked
  none, the household's first voice-template agent answers. Removing the marked agent clears the mark
  in the same save.
- **A household that saved no agents** reads as the two starting templates (`HouseholdAgents.Effective`); its
  first change is made to those, so renaming jonas keeps sugary.
- **An added agent** takes the template's id or the first free `<template>-<n>`; an unshipped template is
  refused.
- **Removing is refused** (one save of one section, never a cascade), each answer saying what to do first,
  for: the default agent; the last agent (none reads as the two templates again); the agent a space opens on;
  an agent still running schedules (`HouseholdSchedules` counts them where the scheduler keeps them,
  `ScheduleKeys` in the deployment's Redis, through `AccountsRedis`, which `RedisAnnouncer` shares;
  unreadable Redis is 503, none configured counts none); an agent a household Telegram bot
  answers as (user's decision, 2026-11-05; rebinding is on the Keys page, `admin-keys.md`).
- **A removed agent's id is never given to another** (`AgentsSection.RemovedIds`, skipped by `FreeId`): an id
  keys its people's conversations, schedules and a bound bot, which a reused id would inherit.
- **Its people's conversations with it are no longer shown** (kept under its id; user's decision, 2026-11-05:
  say so rather than refuse): the confirmation says so, and a chat page open on it names it above the agent
  it fell back to (`TopicsState.RemovedAgentName`) rather than switching silently.
- **Versions** are the name and instructions a change (an edit or a restore) replaced, the last
  `Admin:AgentVersionsKept` per agent, written after the save stood, dropped with the agent — **a table, not
  the section** (`AgentVersions`): no turn reads history, and the section goes to the agent on every view
  and is rewritten whole by the operator's command, which writes none.
- **The page** is `/admin/agents` (`HouseholdAgentsPage`): Remove behind a confirmation, not offered for the
  default or the last agent; the voice choice is a select under the agents. Its E2E test adds and removes its
  own agent, never a seeded one.

## Members

`HouseholdMembersAdmin` over the accounts service's own stores (`MemberStore`, `LinkStore`), never a copy
(`Domain/DTOs/Admin/AdminMembers.cs`). This is the household's one members page; `/accounts/members`
redirects here. Invites and recovery answer `AdminSaved<MemberLink>`; `MemberEdit` (a field left out left as
it is; `readsSugaryLogs` and `usesSugary`: `.claude/rules/sugary.md`), disable and enable (body-less POSTs) answer
`AdminSaved<AdminMembers>`; `AdminProtocol.MemberTelegramGroups` answers a `TelegramGroupCode`.

- **A link or group code is shown once, in the answer that minted it**; only its hash is kept. An invite is
  also redeemable in Telegram (`TelegramLink`, or `TelegramStart` with no bot named). The page shows a minted
  link in a `New link` region until the next change or reload, never again.
- **The last enabled owner** is neither demoted nor disabled (400), checked inside the one `UPDATE`
  (`MemberStore.ChangeRoleAsync`, `DisableAsync`) counting only enabled owners.
- **The operator** is never disabled nor minted a recovery link by an owner (403): their way back in is the
  container's command, and a link would hand an owner the dashboard.
- **A disabled member** is minted no recovery link: `LinkStore.MintRecoveryAsync`'s `RecoveryMint` refuses it
  on every path, each saying why (this route 400, the operator's owner recovery 400, `mint-link --user` exit 1)
  — `.claude/rules/accounts.md`, "Disabled members".
- **Disabling** (`MemberStore.DisableAsync`) sets `User.DisabledAt` and revokes every session; its reach and
  the `PeopleChanged` announcement are `.claude/rules/accounts.md`, "Disabled members". Re-enabling clears the
  column, announced the same way; sessions stay ended. Not told, the change stands with
  `AdminSections.NotToldWarning` (the agent sees it within `Accounts:ConfigurationLifetime`).
- **Role and sees-all reach the member's next turn** (`SessionRefresh` signs a fresh assertion every chat
  turn, as every voice utterance). Two lag: the hub's `TopicViewer` is the connection's, so `PeopleChanged`
  makes each open page renew its connection (`OnAccessChanged`, `ReconnectionEffect` closes a topic no longer
  visible); Telegram keeps an admitting assertion for `LinkCache:Linked`.
- **The page** is `/admin/members` (`HouseholdMembersPage`): role, sees-all, "Uses Sugary" and "Reads everyone's
  Sugary log" saved together, each sent only when changed (`MemberDrafts`, `MemberEdit.ChangesNothing`); Recovery link not offered for the operator or the disabled, Disable (behind a confirmation)
  not for the operator. Its E2E test makes its own people in the stack's second household
  (`PersonElsewhereAsync`).

## Features and spaces

- **Features** (`HouseholdFeaturesAdmin`) reads and saves the section whole as a `JsonObject` (so an unknown feature is refused in the section's words), answered
  `AdminSaved<FeaturesSection>`; announced, so the next turn is built without what was turned off
  (`HouseholdAgents.Offered`, `.claude/rules/household-config.md`). `HouseholdFeaturesAdminTests` proves the
  turn side at `HouseholdAgentSource`. **`apps` is read only here**: an owner's save strips it from what was
  sent and loaded and keeps it as stored; the page shows it as a disabled toggle saying the operator sets it
  (`FeatureHints.Apps`) and sends it back as loaded (`admin-operator.md`, "The apps feature").
- **Spaces** (`HouseholdSpacesAdmin`) reads `AdminSpaces` (in `SpaceListing.Ordered`, the hub's order, beside
  the agents a space may open on) and saves `SpacesSection` whole. Beyond the section's rules:
  - **the default space** (`default`) is never removed while the household has it — removed spaces' topics
    are listed there, alarms push there, the chat's address opens it — only renamed, recoloured, reordered;
  - a name is at most `AdminSpaces.NameLength`; a space opens on one of the household's agents or none;
  - **a slug is set when a space is added and never edited**: a new slug is a new space.
  - **an app space** (`appId`) shares one of the household's published apps with every member. `AdminSpaces.Apps`
    lists them (`AdminSpaceApp`: author by their name now, `reads`, `connect`, `asks`, link) from the apps server's
    listing (`HouseholdAppsClient`, `AccountsSettings.AppsUrl` = `APPSURL`, within `AppsTimeout`), asked on an
    assertion this service signs for the gate-verified owner on `AppChannels.PublishedApps` (as usage's), and
    only for a household with the `apps` feature (else none, nobody asked); null when it could not be asked,
    which costs only a **new** app space or a changed approval (503 `AdminSpaces.AppsUnavailableMessage`): the
    page loads and saves.
    A new app space must name one of the listed apps (400 naming the space); its **name and colour are stamped
    from the app** on every save and answer (as last saved when the server is down), never the page's word;
    **what a space is never changes under its slug** (an app space's app, a chat space never becoming one);
    one space per app, none opening on an agent, the default space never one (the section's rules). Ordered
    among chat spaces as any space. Removing it unshares the app; summaries name the app's id ("added space
    glucose, sharing app a1b2c3").
  - **its approved reach** (`HouseholdSpace.Approved`, what the apps server grants it while shared,
    `.claude/rules/apps.md`) is **only ever what the page sent**, never stamped from the listing — a publish
    landing between load and save is not approved unseen — capped at what the app asks now (`AppReach.Within`;
    nothing approved is none). A save leaving it as loaded leaves it as stored; changing it needs the listing
    (503 `AppsUnavailableMessage`, as a new app space). `AdminSpaces.PendingOf(space)` is what the app asks
    beyond it. Summaries say "changed space glucose's approved reach", never the reach.
- **A removed space's topics are not moved** (`.claude/rules/chat-hub.md`, "Spaces"): the default space lists
  them read side, so a space added back under its slug has its conversations again.
- **A removed space's phones** (`push:space@{tenant}:{slug}`) are left as they are: nothing pushes to a space
  nobody can join, each phone resubscribes in the space its app opens next, and unsubscribing still clears
  every space's set.
- **The pages**: `/admin/features` (`HouseholdFeaturesPage`) and `/admin/spaces` (`HouseholdSpacesPage`:
  Remove not offered for the default space; changes are the page's until "Save spaces" sends the list in
  order; an app space shows its app's facts (`AppSharedFacts`) in place of name, colour and agent — what is
  approved, and apart from it what its published version asks beyond that, with "Approve what it asks"
  (`SpaceDraft.Approve`; the draft carries the loaded approval back, or the merge would take it away) — and
  "Share an app" offers the listed apps no space shares yet (`SpaceDrafts.Shareable`/`Share`: the app's slug,
  else the first free `<slug>-<n>`, approving the reach shown)). `HouseholdSpacesE2ETests` edits a tagged space in the home and never saves features (the home's
  turns read them).

## Apps

`HouseholdAppAsksAdmin` (`AdminProtocol.AppAsks`, `Domain/DTOs/Admin/AdminAppAsks.cs`) reads `AdminAppAsks` (the
`app-asks` section beside the household's published apps, `AdminCappedApp`, from the same listing as the
spaces page, `HouseholdPublishedApps.ReadAsync`: none without the `apps` feature, null when the apps server
could not be asked) and saves the section whole, merged per app id (`admin.md`, "Two owners"). What an
ask does with it is `.claude/rules/apps.md`, "The daily cap".

- **Bounds are the section's rules** (0–`AppAsksSection.MaxDailyCap`, a built app's id), so the operator's
  `set-household-config` is held to them too.
- **An app's own cap is set only for one of the household's published apps**, checked for the caps a save
  **sets or changes** against what it holds now (400 naming the app); one left as loaded is never checked
  again, so a deleted app's cap blocks no other save, and removing one needs nothing. A cap set or changed
  with the listing unavailable is 503 `AdminSpaces.AppsUnavailableMessage`; the household's default and a
  removal still save.
- **Summaries** name the household's cap and apps by id ("Set app a1b2c3's daily cap"), never a value.
- **The page** is `/admin/apps` (`HouseholdAppsPage`): the household's "asks per day, each app, whole household"
  (empty: the deployment's), then each published app's own (empty: the household's), then any cap kept for
  an app not listed, by id, offered only for removal (`AppCapDrafts`). `HouseholdAppsE2ETests` saves the household's in the
  stack's second household (a cap of the home's is read by its app turns), put back after, and the per-app rows
  against a routed answer (`page.RouteAsync`: the stack publishes no apps).

## Own servers

`HouseholdServersAdmin` over the tunnel credentials (`TunnelCredentialStore`,
`.claude/rules/tunnel-credentials.md`) and the agent's relay (`ConnectedServersClient`)
(`Domain/DTOs/Admin/AdminServers.cs`). A mint (`ServerAddition`) answers `AdminSaved<MintedServerCredential>`,
the credential shown once (only its hash is kept); a revoke answers `AdminSaved<AdminServers>`.

- **The list** is every server name holding a live credential, joined by name with the relay's listing, plus
  a name with a tunnel open and no live credential (still shown connected). Relay not answering
  (`ConnectedServersClient` null) is every server `Unknown`, never `Offline`; a name the relay's answer
  lacks is `Offline`. A connected server marks the
  credential its tunnel opened with (`ConnectedServer.CredentialId`), so an owner replacing one sees which to
  keep.
- **A name** is held to `TunnelCredentialProtocol.IsServerName` (a DNS label: it goes into the server's
  synthetic address) before minting, 400 with nothing minted. Minting for a listed name adds a credential;
  nothing is replaced.
- **`lemonade` is minted here too**, for a box reached through the tenant kit: listed with its state, never a
  tool server — no agent dials it (`.claude/rules/household-config.md`). Revoking it closes the forwarder's
  tunnel.
- **Outposts are minted here too**, for `outpost-<NAME>` (`OutpostServerName`), marked by that name as the
  relay does: the verified name makes an outpost, not who minted it.
- **Revoking is the caller's household's alone**: `TunnelCredentialStore.RevokeAsync(id, tenantId)` matches
  both (404 otherwise, nothing revoked or recorded) — `revoke-tunnel-credential` names an id alone, and an
  id is no secret. It publishes the revocation as the
  command does, so the relay closes the tunnel and refuses its reopen. Told, the answer already leaves that
  credential's tunnel out, relying on the relay dropping it from its registry before the close lands
  (`HouseholdServersAdmin.AnswerAsync`'s `closing`); not told, it stands with
  `HouseholdServersAdmin.NotToldWarning` (connected until the tunnel next reopens, refused then).
- **The page** is `/admin/servers` (`HouseholdServersPage`): Revoke behind a confirmation; a minted credential
  is shown in a `New credential` region until the next change or reload, never again.

## Usage

`AdminProtocol.Usage` (`HouseholdUsageAdmin`) → `AdminUsage`: the caller's household's tokens and cost per
agent and per person (`UsageLine`) and its Lemonade use (`LemonadeUsage`) over a month of UTC days. No
`?month=` is the current month on `AccountsClock`; a `month` not `yyyy-MM` is 400. A read: nothing recorded.

- **The figures are the Observability host's**, which this service otherwise never reads:
  `HouseholdUsageClient` asks `HouseholdUsage.Path` at `AccountsSettings.ObservabilityUrl` within
  `UsageTimeout`. A host that could not be asked is **503** `AdminRefusal` (`HouseholdUsageAdmin.UnavailableMessage`), shown
  as "could not be read", never as a month with nothing used; the page clears figures it could not read.
- **The credential is an assertion this service signs** (`Assertions.Of(ForwardedAccount, channel)`) for the
  gate-verified owner on **`AppChannels.Usage`**, a channel no proxy block names: no browser holds one, the
  admin assertion is never passed on, and no secret exists for it.
- **The household is the assertion's**: the host admits that one path for a `usage` assertion with `owner`,
  and the route reads no `?household=` (`.claude/rules/observability.md`).
- **Per person is by account** (`TokenUsageEvent.UserId`), labelled here with the member's display name now
  (`MemberStore.ListAsync`) over Observability's `UsageLine.Id`; a call with no account is a line under its
  `Sender` with no id. Per agent is by id under its latest name (`MetricsQueryService.AgentLabels`).
- **History is a month deep** (daily roll-ups expire after thirty days); the page, `/admin/usage`
  (`HouseholdUsagePage`), says so.
- The operator reads every household's figures on the dashboard (`HouseholdPicker`,
  `.claude/rules/observability.md`), not through this route.

## Tests

- **Agents**: `HouseholdAgentsAdminTests`; `HouseholdAdminE2ETests` works in the stack's second household,
  since an added agent has every page of the household refetch its agents.
- **Members**: `HouseholdMembersAdminTests` checks a disabled member is nobody on every way in and that the
  agent's reader says so (`HouseholdAccountStanding`); `ChatMonitorAssertionTests`,
  `ChatMonitorDelegationTests` (`FakeAccountStanding`); `HouseholdMembersE2ETests`.
- **Own servers**: `HouseholdServersAdminTests` against the real relay (`RelayHost`, `TunnelledServer`; the
  relay's port taken first, since each is told the other's address); `HouseholdServersE2ETests` uses its own
  tagged server in the home, which no agent names.
- **Usage**: `HouseholdUsageAdminTests` runs the accounts service and the Observability host's shipped gate
  and API in process for two households (own figures only, `?household=` ignored, only an owner's usage
  assertion admitted, 503 when nobody answers). `HouseholdUsageE2ETests` sees the unavailable notice (no
  Observability host in the E2E stack) and routes the page's read (`page.RouteAsync`).
  `DashboardHouseholdFilterE2ETests` runs on `DashboardAdminE2EFixture` (`admin-operator.md`).
