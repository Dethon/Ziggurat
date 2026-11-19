---
paths:
  - "Accounts/Admin/HouseholdAgentsAdmin.cs"
  - "Accounts/Admin/AgentVersions.cs"
  - "Accounts/Admin/HouseholdSchedules.cs"
  - "Accounts/Admin/HouseholdMembersAdmin.cs"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
  - "Accounts/Admin/HouseholdSpacesAdmin.cs"
  - "Accounts/Admin/HouseholdServersAdmin.cs"
  - "Accounts/Admin/HouseholdUsageAdmin.cs"
  - "Accounts/Admin/HouseholdUsageClient.cs"
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

## Agents

`HouseholdAgentsAdmin` over the agents section, `AgentVersions` beside it (`Domain/DTOs/Admin/AdminAgents.cs`):

- `GET /household/agents` → `AdminAgents`: the default, each `AdminAgent` (template none for a seeded
  definition; description and model shown, never edited; the whole `definition` from
  `HouseholdAgents.DefinitionOf`, before the deployment's servers are weighed, shown read only as indented
  JSON by `AgentDefinitionText`, never a bot token — user, 2026-11-13), the shipped templates, the
  instructions limit.
- `POST /household/agents` (`AgentAddition { template, name }`); `PUT`/`DELETE /household/agents/{agentId}`
  (`AgentEdit { name, customInstructions }`); `GET /household/agents/{agentId}/versions`
  (`AdminAgentVersion`, newest first) and `POST .../versions/{versionId}/restore`.
- `PUT /household/voice-agent` (`VoiceAgentChoice { agentId }`, null for none — beside the agents, not under
  them, where it would read as an agent's id).
- Every change answers `AdminSaved<AdminAgents>`, the whole list, with `voiceAgent` (the one marked) and
  `answersAloud` (the one that does, `HouseholdAgents.VoiceAgentOf`, or null).

Rules:

- **Which agent answers aloud is the owner's too**: any household agent that holds a conversation, whatever
  its template — an agent with `no-tools` (the Sugary estimator) answers one request and is never offered
  (`HouseholdAgent.MayAnswerAloud`, `AdminAgent.MayAnswerAloud`; the section's rule refuses the mark). A
  Jonas answers a satellite under the voice rules as a Nabu does (`.claude/rules/channels.md`). Marked
  none, the household's first agent of the voice template answers. The section's rule refuses a mark naming
  none of its agents; removing the marked agent clears the mark in the same save. Logged "Marked agent jonas
  to answer aloud" / "Marked no agent to answer aloud". The page is a select under the agents, saying who
  answers now.
- **Only the name and the custom instructions are the owner's** of an agent itself; nothing else in an edit
  binds (a model sent beside them is ignored). They live where the section says: a template entry's
  `name`/`customInstructions`, a seeded definition's `definition.name`/`definition.customInstructions` (the
  home's jack, jonas, nabu and sugary) — so the home's agents are renamed like any other.
- **Limits are the admin API's**, beyond the section's rules: a name 1–`AdminAgent.NameLength` (100)
  characters, instructions at most `Admin:AgentInstructionsLength` (appsettings, 4000) — they are in every
  turn's prompt. The operator's `set-household-config` is not held to them.
- **A household that saved no agents** reads as the two starting templates (`HouseholdAgents.Effective`);
  its first change is made to those, so renaming jonas keeps sugary.
- **An added agent** takes the template's id, or the first free `<template>-<n>`. A template this build does
  not ship is refused.
- **Removing** is refused (one save of one section, never a cascade) for:
  - the default agent ("answers by default, so it stays: rename it…") — nothing else names which agent
    answers a message that names none;
  - the last agent — a section with none reads as the two starting templates again;
  - the agent a space opens on ("The Kitchen space opens on Chef, so it stays: pick another agent for that
    space first");
  - an agent still running schedules ("Chef runs 2 schedules, so it stays: ask it to delete them, or to move
    them to another agent, first"), which would otherwise be refused at every fire with nobody told.
    `HouseholdSchedules` counts them where the scheduler keeps them (`ScheduleKeys`, the deployment's Redis,
    through `AccountsRedis`, which `RedisAnnouncer` shares); an unreadable Redis refuses the removal 503, no
    Redis configured counts none;
  - an agent a household Telegram bot answers as ("The household's Telegram bot answers as Chef, so it
    stays: bind the bot to another agent first"; user's decision, 2026-11-05) — rebinding is saving the
    bot's token under another agent on the Keys page (`admin-keys.md`).
- **A removed agent's id is never given to another** (`AgentsSection.RemovedIds`, appended by the removal,
  skipped by `FreeId`): an id keys its people's conversations, schedules and a bound bot, none of which go
  with the agent, so a reused id inherited all of it.
- **Its people's conversations with it are no longer shown** (kept under its id, listed nowhere; user's
  decision, 2026-11-05: say so rather than refuse): the confirmation says so, and a chat page on it when it
  went names it above the agent it fell back to (`TopicsState.RemovedAgentName`, until the person picks an
  agent or opens a conversation) — rather than switching agents silently.
- **Versions** are the name and instructions a change replaced (an edit's or a restore's, so a restore is
  undone the same way), the last `Admin:AgentVersionsKept` (5) per agent, by whom and when, written after the
  save stood and dropped with the agent. **A table, not the section**: no turn reads history, and the
  section is sent to the agent on every view, migrated by every step and rewritten whole by the operator's
  command, which writes no version.
- Summaries name the agent's id and the fields changed ("Changed agent jonas's name, custom instructions",
  "Added agent jonas-2 from the jonas template", "Restored a version of agent jonas", "Removed agent
  jonas-2"), never a name or instructions.
- **The page** is `/admin/agents` (`HouseholdAgentsPage`): Remove behind a confirmation, not offered for the
  default or the last agent. Its E2E test adds and removes its own agent, never touching seeded ones.

## Members

`HouseholdMembersAdmin` over the accounts service's stores (`MemberStore`, `LinkStore`), never a copy
(`Domain/DTOs/Admin/AdminMembers.cs`):

- `GET /household/members` → `AdminMembers` (each an `AdminMember`, role `owner`/`member`).
- `POST /household/members/invites` (`MemberInvite { displayName, role }`) and `POST .../{userId}/recovery`,
  answering `AdminSaved<MemberLink>` — the link once, never again (only its hash is kept); an invite also as
  the bot's deep link (`TelegramLink`) or, with no bot named, `/start <token>` (`TelegramStart`), which the
  bot redeems in place of a passkey.
- `PUT .../{userId}` (`MemberEdit { role?, seesAll?, readsSugaryLogs? }`, a field left out left as it is;
  `readsSugaryLogs` lets the member's agent read everyone's Sugary log, `.claude/rules/sugary.md`) and
  `POST .../{userId}/disable` / `.../enable`, answering `AdminSaved<AdminMembers>`, the whole list. A
  body-less POST is fine: the path says whom.
- `POST /household/members/telegram-groups` (`AdminProtocol.MemberTelegramGroups`) mints a group code,
  answered once as `TelegramGroupCode { bind: "/bind <code>", expiresAt }`, recorded as "Minted a Telegram
  group code", never its characters.
- This is the household's one members page; the accounts service's `/accounts/members` redirects here.

Rules:

- **The last enabled owner** is neither demoted nor disabled (400): checked inside the one `UPDATE`
  (`MemberStore.ChangeRoleAsync`, `DisableAsync`), counting only enabled owners — demoting the last enabled
  one beside a disabled one is refused too.
- **The operator** is never disabled nor minted a recovery link by an owner (403): their account is the
  deployment's way onto its dashboard and back in, which the container's command keeps.
- **A disabled member** is minted no recovery link (400: it would open nothing until re-enabled);
  `LinkStore.MintRecoveryAsync`'s `RecoveryMint` refuses it on every minting path, each saying why rather
  than handing out a dead link — this route, the operator's owner recovery (400), `mint-link --user` (exit 1,
  "is disabled") (`.claude/rules/accounts.md`, "Disabled members").
- **Disabling** (`MemberStore.DisableAsync`) sets `User.DisabledAt` and revokes every session; what it
  reaches, and the `PeopleChanged` announcement the agent refuses their turns by, are
  `.claude/rules/accounts.md`, "Disabled members". Re-enabling clears the column, announced the same way;
  their sessions stay ended (they sign in again with a kept passkey or a recovery link) and everything else
  of theirs answers for them again. Not told, the change stands with `AdminSections.NotToldWarning` (the
  agent sees it within `Accounts:ConfigurationLifetime`).
- **Role and sees-all reach the member's next turn**: the chat hub asks a fresh assertion every turn
  (`SessionRefresh`), signed with the roles as they are then, as does every voice utterance. Two things lag:
  - the hub's own topic view (`TopicViewer`, its sees-all) is the connection's, so the change is announced
    (`PeopleChanged`) and each open page renews its connection (`OnAccessChanged`): a grant shows the topics
    at once, a withdrawal takes them away and closes one open (`ReconnectionEffect`);
  - Telegram keeps the assertion that admitted a sender for `LinkCache:Linked` (5 minutes).
- Summaries name the member's id and what changed, never a name or any part of a link: "Invited somebody as
  a member" / "as an owner", "Changed member <id>'s role, sees all conversations, reads everyone's Sugary
  log", "Disabled member <id>", "Re-enabled member <id>", "Minted a recovery link for member <id>".
- **The page** is `/admin/members` (`HouseholdMembersPage`): role, sees-all and "Reads everyone's Sugary
  log" saved together; Recovery link not offered for the operator or the disabled, Disable (behind a
  confirmation) not for the operator. A minted link is shown in a `New link` region until the next change or
  a reload, never again. Its E2E test makes its own people in the stack's second household
  (`PersonElsewhereAsync`) and changes only them.

## Features and spaces

- **Features** (`HouseholdFeaturesAdmin`): `GET`/`PUT /household/features` reads and saves the section whole
  (a `JsonObject`, as settings does, so an unknown feature is refused in the section's words), answered
  `AdminSaved<FeaturesSection>`, recorded as "Changed the scheduling, memory features" — names, never on or
  off. The save is announced, so the next turn is built without what was turned off
  (`HouseholdAgents.Offered`, `.claude/rules/household-config.md`); the turn side is proven at
  `HouseholdAgentSource` over the agent's own reader (`HouseholdFeaturesAdminTests`).
- **Spaces** (`HouseholdSpacesAdmin`): `GET /household/spaces` answers `AdminSpaces { spaces, agents }` — the
  spaces in order (`SpaceListing.Ordered`, the hub's) and the agents a space may open on
  (`HouseholdAgents.Names`), so the page needs one load. `PUT` takes the section whole (`SpacesSection`,
  names trimmed), answered `AdminSaved<AdminSpaces>`. Beyond the section's rules:
  - **the default space** (`default`) is never removed while the household has it — a removed space's topics
    are listed there, alarms push there and the chat's own address opens it — only renamed, recoloured and
    reordered;
  - a name is at most `AdminSpaces.NameLength` (100); a space opens on one of the household's agents or none;
  - a slug is set when a space is added and never edited: a new slug is a new space, and the old one's topics
    go to the default space;
  - recorded per space by slug: "Added space kitchen; removed space attic; changed space default's name,
    colour, default agent, order".
- **A removed space's topics are not moved** (`.claude/rules/chat-hub.md`, "Spaces"): the default space
  lists them read side, so a removal is one save and a space added back under its slug has its conversations
  again.
- **A removed space's phones** (`push:space@{tenant}:{slug}`) are left as they are: nothing pushes to a space
  nobody can join (a reply's push goes to its session's space), the set is never read again, each phone
  subscribes in whatever space its app opens next (a removed slug opens the household's first), and
  unsubscribing still clears it from every space's set.
- **The pages**: `/admin/features` (`HouseholdFeaturesPage`, features saved together) and `/admin/spaces`
  (`HouseholdSpacesPage`: Remove not offered for the default space; every change is the page's until "Save
  spaces" sends the list in its new order). `HouseholdSpacesE2ETests` adds, edits and removes a tagged space
  in the home; it opens the features page and never saves it (the home's turns read features).

## Own servers

`HouseholdServersAdmin` over 4a's tunnel credentials (`TunnelCredentialStore`, `.claude/rules/tunnel-credentials.md`)
and the agent's relay (`ConnectedServersClient`, the household list's client)
(`Domain/DTOs/Admin/AdminServers.cs`):

- `GET /household/servers` → `AdminServers` (each `AdminServer`: a `ServerConnection`
  `Connected`/`Offline`/`Unknown`, its tools, its live credentials as `AdminServerCredential` with in-use).
- `POST /household/servers` (`ServerAddition { name }`) → `AdminSaved<MintedServerCredential>`, the
  credential once, never again (only its hash is kept).
- `POST /household/servers/credentials/{credentialId}/revoke` → `AdminSaved<AdminServers>`.

Rules:

- **The list** is every server name holding a live credential of the household, joined by name with the
  relay's listing — plus a name with a tunnel open but no live credential (revoked while the relay could not
  be told), still shown connected. Relay not answering (`ConnectedServersClient` null): every server
  `Unknown`, never offline; answered without it: `Offline`. Connected carries the tunnel's opening time and
  the tools it listed just now (null: did not answer), and marks the credential the tunnel opened with as in
  use (`ConnectedServer.CredentialId`), so an owner replacing a credential (mint, restart the server with it,
  revoke the old) sees which to keep.
- **A name** is held to `TunnelCredentialProtocol.IsServerName` (a DNS label: it goes into the server's
  synthetic address) before minting, refused 400 in those words with nothing minted. Minting for a listed
  name adds a second credential; nothing is replaced.
- **`lemonade` is minted here too**, for a household whose Lemonade box is reached through its tenant kit
  (the Keys page says so): listed with its state, never a tool server — no agent dials it
  (`.claude/rules/household-config.md`). Revoking it closes the forwarder's tunnel, and the box offers no
  models from the agent's next refresh.
- **Outposts are minted here too**: a machine's credential is for `outpost-<NAME>` (`OutpostServerName`),
  and the list marks it by that name, as the relay does — the verified name makes an outpost, not who minted
  it, and a household's machines are its owners'.
- **Revoking is the caller's household's alone**: `TunnelCredentialStore.RevokeAsync(id, tenantId)` matches
  household and id together (404 otherwise, nothing revoked or recorded) — `revoke-tunnel-credential` names
  an id alone, and an id is no secret. It publishes the revocation as the command does, so the relay closes
  the tunnel at once and refuses its reopen. Told, the answer leaves out that credential's tunnel without
  waiting for the close (the relay drops it from its registry first thing); not told, the revocation stands
  with `HouseholdServersAdmin.NotToldWarning` — the server stays connected until its tunnel next reopens, and
  is refused then.
- Summaries name the server, never the credential: "Minted a tunnel credential for server <name>", "Revoked
  a tunnel credential of server <name>".
- **The page** is `/admin/servers` (`HouseholdServersPage`): Revoke behind a confirmation. A minted
  credential is shown in a `New credential` region until the next change or a reload, never again; the
  effect asks the list again after a mint.

## Usage

`GET /household/usage?month=yyyy-MM` (`AdminProtocol.Usage`, `HouseholdUsageAdmin`) answers `AdminUsage`:
the caller's household's tokens and cost per agent and per person (`UsageLine`, costliest first) and its
Lemonade use (`LemonadeUsage`). A month is its UTC days (metrics are kept by UTC day);
none named is the current one on `AccountsClock`; anything but `yyyy-MM` is 400. A read: nothing recorded.

- **The figures are the Observability host's**, which this service otherwise never reads:
  `HouseholdUsageClient` asks its usage route (`HouseholdUsage.Path`, `/api/usage?from=&to=`) at
  `AccountsSettings.ObservabilityUrl` (`OBSERVABILITYURL` in compose, `http://observability:8080` by default)
  within `UsageTimeout` (appsettings, 10 s). A host that could not be asked — down, refusing, slow — is
  **503** `AdminRefusal` (`HouseholdUsageAdmin.UnavailableMessage`), shown as "could not be read", never as a
  month with nothing used.
- **The credential is an assertion this service signs** (`Assertions.Of(ForwardedAccount, channel)`) for the
  owner the gate verified, for **`AppChannels.Usage`** (`usage`), a channel no proxy block names: no browser
  is handed one, and the admin assertion is never passed on. The host already verifies this service's keys
  (`OperatorGate`), so no secret exists for it.
- **The household is the assertion's**: the host admits that one path for a `usage` assertion with `owner`,
  and the route reads no `?household=` (`.claude/rules/observability.md`).
- **Per person is by account** (`TokenUsageEvent.UserId`, the account a call was billed to), so a rename or a
  memory call naming only the id does not split a person. Observability labels each with the latest name
  their calls carried (`MetricsQueryService.PersonLabels`) and answers the id beside it (`UsageLine.Id`);
  this route replaces that with the member's display name now (`MemberStore.ListAsync`), so a rename shows
  over the whole month. A call with no account (from before accounts were carried, the guest) is a line
  under its `Sender` with no id. Per agent is by id under its latest name, as the dashboard labels agents
  (`MetricsQueryService.AgentLabels`).
- **History is a month deep**: daily roll-ups expire thirty days after writing, so an earlier month shows
  only the days still kept; the page says so.
- **The page** is `/admin/usage` (`HouseholdUsagePage`). Figures that could not be read are cleared, never
  left on show under another month.

The operator reads every household's figures on the dashboard's own pages through its household filter
(`HouseholdPicker`, `.claude/rules/observability.md`), not through this route.

## Tests

- **Agents**: `HouseholdAgentsAdminTests` (seam 1); `HouseholdAdminE2ETests` (seam 2) works in the stack's
  second household, since an added agent has every page of the household fetch its agents again.
- **Members**: seam 1 `HouseholdMembersAdminTests` checks a disabled member is nobody on every way in (the
  session check, the session refresh, `resolve` for voice and Telegram, `web-link`, a recovery link,
  `mint-session`) and that the agent's own reader says so (`HouseholdAccountStanding` over
  `HouseholdConfigurationClient` and a real Redis); seam 4 `ChatMonitorAssertionTests` and
  `ChatMonitorDelegationTests` with a `FakeAccountStanding`; seam 2 `HouseholdMembersE2ETests`.
- **Own servers**: seam 1 `HouseholdServersAdminTests` against the real relay (`RelayHost`,
  `TunnelledServer` from `Tests/Integration/Tunnels`; the relay's port taken first, since accounts is told
  the relay's address and the relay the accounts service's): a server added through the route connects
  with the credential shown, is listed with its tools then offline, and a credential revoked there closes
  its tunnel and is refused on reopen. Seam 2 `HouseholdServersE2ETests`: its own tagged server in the home,
  which no agent names, listed offline (the stack's real agent is asked) and revoked.
- **Usage**: seam 1 `HouseholdUsageAdminTests` runs the accounts service and the Observability host's shipped
  gate and API in process (`AddOperatorGate` over the accounts server's JWKS, `MetricsQueryService` on a
  `RedisFixture`, both on the accounts clock, the host's port taken first), with roll-ups written by
  `MetricsCollectorService.ProcessEventAsync` for two households: each owner reads their own and never the
  other's, a `?household=` naming another is ignored, every assertion but an owner's usage one is refused,
  and a host nobody answers on is 503. Seam 2 `HouseholdUsageE2ETests`: the E2E stack runs no Observability
  host, so the real read is the unavailable notice, and a month's figures come from answering the page's
  read (`page.RouteAsync`). Seam 3 `DashboardHouseholdFilterE2ETests` on `DashboardAdminE2EFixture`
  (`admin-operator.md`), publishing events on its Redis (`RedisConnectionString`, `metrics:events`).
