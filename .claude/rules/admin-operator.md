---
paths:
  - "Accounts/Admin/HouseholdListAdmin.cs"
  - "Accounts/Admin/HouseholdSuspensionAdmin.cs"
  - "Accounts/Tunnels/ConnectedServersClient.cs"
  - "Domain/DTOs/Admin/AdminProtocol.cs"
  - "Dashboard.Client/Pages/Households.razor"
  - "Dashboard.Client/State/Households/**"
  - "Dashboard.Client/Effects/HouseholdsEffect.cs"
  - "Dashboard.Client/Services/HouseholdsAdminService.cs"
  - "Tests/Integration/Accounts/HouseholdListAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdSuspensionAdminTests.cs"
  - "Tests/Integration/Accounts/SuspensionCoverageTests.cs"
  - "Tests/E2E/Dashboard/HouseholdListE2ETests.cs"
  - "Tests/E2E/Dashboard/DashboardHouseholdFilterE2ETests.cs"
  - "Tests/E2E/Fixtures/DashboardAdminE2EFixture.cs"
---

# The admin API: the operator's households

See also: `admin.md` (routes, gate, answers, change log, the proxy), `admin-keys.md`,
`admin-owner-pages.md`.

## The household list (dashboard)

The operator's households, from the dashboard (`HouseholdListAdmin`, `/households` in `Dashboard.Client`):

- **`GET /operator/households`** answers every household as a `HouseholdListEntry`, the home first: owners
  and members (`HouseholdPerson`, the operator flagged), **connected servers** and **last seen**.
  - The servers are the agent's relay's, which lives in the agent's serving process alone:
    `ConnectedServersClient` asks the agent's listing (`GET {AgentUrl}/internal/tunnels/{householdId}`,
    `ConnectedServers.Path`, the one `list-household-servers` reads) with `CALLERS__agent` as bearer — the
    credential the agent presents here, which compose feeds the agent API's own secret, so no new secret
    (`DeploymentConformanceTests.TheAccountsService_AsksTheAgentsRelayWithTheAgentApisSecret`). An agent not
    configured, down, refusing or slower than `AgentListingTimeout` (appsettings, 15 s: the relay asks each
    server for its tools for up to ten) leaves that household's servers **null**, shown as unknown, never a
    failed list. Owners' pages read the same client (`admin-owner-pages.md`, "Own servers").
  - **Last seen** is the latest `Session.LastSeenAt` of any household session — the session check's record,
    at its five-minute grain — so Telegram- or voice-only use never moves it; spend is the usage view's,
    read from the metrics.
- **`POST /operator/households`** `NewHousehold { name, ownerName }` creates the household (`Tenant`), its one
  owner (a `User` with no passkey yet) and the owner's `LinkKind.Recovery` link in one `SaveChanges`
  (`LinkStore.CreateHouseholdAsync`), answered `AdminSaved<OwnerLink>`; the name is held to the settings'
  length and the owner's to `Profile.Read`, refused 400 in those words with nothing written. A recovery
  rather than an invite, so the owner is listed (and recoverable) before the link is used. **Nothing of its
  configuration is written**: an unsaved section reads as its defaults — name from the tenant row, the one
  default space, the default features — and a section naming no agent is the two templates
  (`HouseholdAgents.Starting`), so its first turn has agents.
- **`POST /operator/households/{householdId}/owners/{userId}/recovery`** mints a recovery link for an owner
  of any household, answered `AdminSaved<OwnerLink>`: 404 for nobody of that id in that household; 400 for a
  member (their owners mint theirs), the operator (their way back is their account page or the container
  command: a stolen dashboard session must not become a passkey), or an owner another owner disabled
  (`RecoveryMint.Disabled`: the link would open nothing until re-enabled).
- Both mints are recorded on the household they are for, the link never in the summary. A link is in the
  answer alone; the page holds it in its state until "Done" or the page is left, and never asks again.
- **Suspension** (`HouseholdSuspensionAdmin`): **`PUT /operator/households/{householdId}/suspension`**
  `HouseholdSuspension { suspended }` sets `Tenant.SuspendedAt` (kept when already suspended, cleared on
  resume), answered `AdminSaved<HouseholdSuspension { suspended, since }>`; the home is 400 (the operator's
  own: nothing would be left to resume it from), no such household 404. Recorded "Suspended the household" /
  "Resumed the household" only when it changed; announced every time (asking again is the retry):
  `HouseholdConfigSaved(id, HouseholdConfigProtocol.SuspensionChanged)` so every reader of the household's
  configuration drops what it kept, and on suspending `HouseholdSuspended(id)` on
  `TunnelCredentialProtocol.SuspendedChannel` so the relay closes its open tunnels; not told stands with
  `NotToldWarning`. **Nothing is deleted.** Each place that lets a household in reads the row:
  - the session check, and the account and members pages, which read their own cookie (`PageGuard` answers
    `SessionCheck.Suspended` first, so no invite, recovery link, group code, passkey or profile change),
    `.claude/rules/accounts.md`;
  - the channels' ways in — `resolve`, the guest, the Telegram `start` (nothing spent) and `web-link` —
    answer `SuspendedHouseholdAnswer` (423), neither a person nor nobody, so no channel vouches for its
    people and the bot tells them why (`TelegramGate.SuspendedAnswer`);
  - the agent's view (`AgentHouseholdConfig.Suspended`), refused per message in `ConversationGroup` — every
    turn and every fire, delegated or not, by the household its server keeps the object under
    (`ChannelMessage.HouseholdId`, a schedule's `TenantId`) as well as its person's
    (`.claude/rules/channels.md`);
  - dreaming skips the household (`MemoryDreamingService`, the same view it reads the key from), and memory
    extraction asks nothing for a turn admitted before the suspension (`MemoryExtractionWorker`, the same
    view; likewise a member disabled since their turn);
  - the tunnel verify (`TunnelCredentialRefusal.Suspended`) and a household server's back-call
    (`TunnelCredentialStore.LiveAsync` answers none).

  Each reads the column in a query of its own shape, so nothing gathers them but a list:
  `SuspensionCoverageTests` places every route the accounts service maps, by path, with how it refuses a
  suspended household or why it need not, and fails on a route it does not place — a new way in says what
  suspension does to it before it ships.

  The list's `HouseholdListEntry.SuspendedAt` and the row's Status cell (Suspend / Resume, none for the home)
  are the dashboard's.
- **Its open pages are told at once**: the chat hub hears `SuspensionChanged` as it hears people changing
  (`HouseholdAgentsAnnouncer`) and sends the household's pages `OnAccessChanged`; each opens a fresh
  connection, which the session check refuses as suspended, and the page shows the household's notice
  (rather than staying "Connected" until a reload). **And back again once resumed**: the chat client goes to
  the notice as `/accounts/account?returnUrl=<where it was>`, and the notice (`SessionCheck.Suspended`,
  `data-suspended`, with `data-return` only for a return `ReturnUrls` accepts) asks for itself again every
  20 s (`PasskeyScript`) and goes back once it is no longer 403 — a reload would have kept them on the
  account page.
- **Not stopped by it, deliberately**: the chat hub's per-turn session refresh still answers, so an open
  page's turn reaches the agent, which answers `SuspendedAnswer` — a refused refresh would end the stream
  "signed out", the wrong words. `/accounts/me` still names the person. A refused fire was emitted all the
  same, so the scheduler advances a cron and deletes a one-shot as if it ran: a one-shot due while suspended
  is gone on resume (kept so by the user, 2026-11-05), and the dashboard's Suspend confirmation and the
  members page (for a disabled member's) say so.

## Tests

- **The list**: seam 1 `HouseholdListAdminTests` — `AccountsServer.Admin(session).PostAsync`, a person of
  another household by `SeedPersonAsync(household, name, role)`, the agent's listing a Kestrel stub behind
  the shared secret (`AccountsSettings.AgentUrl` at it, `Callers["agent"]` the secret). The owner's link
  enrolling a passkey into the new household is a ceremony (`PasskeyCeremoniesTests`).
- **Seam 3** `HouseholdListE2ETests` on `DashboardAdminE2EFixture` (collection `DashboardAdminE2E`):
  accounts, observability and a Caddy mirroring the dashboard's host; the operator signed in by
  `mint-link --operator` and `mint-session` (`CreateOperatorPageAsync`, which also answers the home's id),
  anybody else by `CreatePersonPageAsync(name, role)`, the relay's listing a stub a test sets with
  `Connect(householdId, server)`. Its stack is its own, so a test may create, change and suspend households
  freely; it is the fixture usage and suspension extend. Its accounts service announces on the fixture's
  Redis; `OwnerSessionAsync(operatorPage, householdName)` mints a session for the owner of a household the
  operator created (found through the operator page's list), and `OpenAsAsync(session, path)` asks the host
  as that person's browser would over HTTP — the base fixture keeps one page open at a time, so a second
  person cannot have a page beside the operator's.
- **Suspension**: seam 1 `HouseholdSuspensionAdminTests` (the route, the home refused, the announcements on a
  `RedisFixture`, the tunnel verify and back-call, the agent's reader seeing it and resuming) beside
  `SessionCheckTests` (the notice and the 403); seam 4 `ChatMonitorAssertionTests` and
  `ChatMonitorDelegationTests` over `MonitorTestMocks.Households(suspended, from)` — suspended from its n-th
  asking, so a running conversation is refused from its next message — and `TunnelRelayTests` (its tunnels
  closed `Suspended`, another household's left open).
- **Seam 5** `StackSmokeTests.AHouseholdTheOperatorCreates_IsAnsweredByTheAgentItsOwnerNamed_UntilItIsSuspended`
  on the smoke stack (`scripts/smoke-stack.sh`): create and suspend through the dashboard's host, the owner
  signed in by `mint-session --user` from the create answer's `UserId`, the OpenRouter key and the agent's
  name through the main host's household routes, every call with the admin header. The stack's accounts
  service has a master key made for its smoke state and its `KEYPROBES__*` at `smoke-keys`, a stub that
  takes every key without "refused" or "unreachable" in it (`DockerCompose/docker-compose.override.smoke.yml`),
  so the key set is the deployment's own, read from the agent's user secrets, and only the turn reaches
  OpenRouter. Each run makes its own household, suspends it (the open page goes to the notice) and resumes it
  (the notice goes back to the chat by itself); `down` removes it.
