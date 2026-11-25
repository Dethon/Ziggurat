---
paths:
  - "Accounts/Admin/HouseholdListAdmin.cs"
  - "Accounts/Admin/HouseholdSuspensionAdmin.cs"
  - "Accounts/Admin/HouseholdAppsFeatureAdmin.cs"
  - "Accounts/Tunnels/ConnectedServersClient.cs"
  - "Domain/DTOs/Admin/AdminProtocol.cs"
  - "Dashboard.Client/Pages/Households.razor"
  - "Dashboard.Client/State/Households/**"
  - "Dashboard.Client/Effects/HouseholdsEffect.cs"
  - "Dashboard.Client/Services/HouseholdsAdminService.cs"
  - "Tests/Integration/Accounts/HouseholdListAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdSuspensionAdminTests.cs"
  - "Tests/Integration/Accounts/HouseholdAppsFeatureAdminTests.cs"
  - "Tests/Integration/Accounts/SuspensionCoverageTests.cs"
  - "Tests/E2E/Dashboard/HouseholdListE2ETests.cs"
  - "Tests/E2E/Dashboard/DashboardHouseholdFilterE2ETests.cs"
  - "Tests/E2E/Fixtures/DashboardAdminE2EFixture.cs"
---

# The admin API: the operator's households

See also: `admin.md` (routes, gate, answers, change log, the proxy), `admin-keys.md`,
`admin-owner-pages.md`.

## The household list (dashboard)

`HouseholdListAdmin`, `/households` in `Dashboard.Client`:

- **`GET /operator/households`** answers every `HouseholdListEntry`, the home first, with people,
  **connected servers**, **last seen** (`Session.LastSeenAt`, so Telegram/voice-only use never moves
  it) and whether its **apps** feature is on (a household that never saved its features reads off).
  - Connected servers come from the agent's relay (`ConnectedServersClient` →
    `GET {AgentUrl}/internal/tunnels/{householdId}`, `ConnectedServers.Path`) with `CALLERS__agent` as
    bearer — no new secret
    (`DeploymentConformanceTests.TheAccountsService_AsksTheAgentsRelayWithTheAgentApisSecret`). An agent
    unconfigured, down, refusing or slower than `AgentListingTimeout` (15 s, kept above the relay's
    ten-second tool ask per server — `AccountsSettings`) leaves that household's servers
    **null** (unknown), never a failed list. Owners' pages read the same client (`admin-owner-pages.md`,
    "Own servers").
- **`POST /operator/households`** `NewHousehold { name, ownerName }` creates the `Tenant`, its owner and the
  owner's `LinkKind.Recovery` link in one `SaveChanges` (`LinkStore.CreateHouseholdAsync`) — a recovery
  link, not an invite, so the owner is listed (and recoverable) before it is used. Answered
  `AdminSaved<OwnerLink>`, as is the recovery mint; a bad household or owner name is 400 with nothing written. **Nothing of
  its configuration is written**: unsaved sections read as their defaults (`household-config.md`,
  "Sections") and no agents is the two templates (`HouseholdAgents.Starting`), so its first turn has
  agents.
- **`POST /operator/households/{householdId}/owners/{userId}/recovery`** mints an owner's recovery link;
  404 for nobody of that id in that household; 400 for a member, the operator (a stolen dashboard session must not become a passkey) or a disabled
  owner (`RecoveryMint.Disabled`).
- Both mints are recorded in the change log of the household they are for; the link is in the answer
  alone, never in the log; the page holds it until "Done" and never
  asks again.
- **Suspension** (`HouseholdSuspensionAdmin`): **`PUT /operator/households/{householdId}/suspension`**
  `HouseholdSuspension { suspended }` sets `Tenant.SuspendedAt` (kept if already suspended, cleared
  on resume), answered `AdminSaved<HouseholdSuspension { suspended, since }>`; no such household is
  404, the home is 400 (nothing would be left to resume it from). **Nothing is
  deleted.** Recorded only when it changed; announced every time (asking again is the retry):
  `HouseholdConfigSaved(id, HouseholdConfigProtocol.SuspensionChanged)` so every configuration reader
  drops what it kept, and on suspending `HouseholdSuspended(id)` on
  `TunnelCredentialProtocol.SuspendedChannel` so the relay closes the household's tunnels; not told
  stands with `NotToldWarning`. Each way in reads the row itself:
  - the session check and the account/members pages (`PageGuard` answers `SessionCheck.Suspended` first),
    `.claude/rules/accounts.md`;
  - the channels' ways in answer `SuspendedHouseholdAnswer` (423) (`.claude/rules/channel-identity.md`, "Suspended households");
  - the agent's view (`AgentHouseholdConfig.Suspended`), refused per message in `ConversationGroup` —
    every turn and every fire, delegated or not, by the household its server keeps the object under
    (`ChannelMessage.HouseholdId`, a schedule's `TenantId`) as well as its person's
    (`.claude/rules/channels.md`);
  - dreaming skips the household (`MemoryDreamingService`), and extraction asks nothing for a turn
    admitted before the suspension (`MemoryExtractionWorker`);
  - the tunnel verify (`TunnelCredentialRefusal.Suspended`) and a household server's back-call
    (`TunnelCredentialStore.LiveAsync`).

  Nothing gathers them but a list: `SuspensionCoverageTests` places every accounts route by path with
  its stance and fails on a route it does not place — a new way in says what suspension does to it
  before it ships.
- **The apps feature** (`HouseholdAppsFeatureAdmin`): **`PUT /operator/households/{householdId}/features/apps`**
  `HouseholdAppsFeature { on }` sets the features section's `apps` alone, the operator's alone (owners only
  see it: `admin-owner-pages.md`), the home included; answered `AdminSaved<HouseholdAppsFeature>`; no such
  household is 404. Saved through the section's save and announced, so the household's agents are offered
  `/apps` from their next turn (`HouseholdAgents.Offered`); not told stands with `NotToldWarning`. Recorded
  on that household only when it changed ("Turned the apps feature on"). Runs in the household's turn
  (`admin.md`, "Two owners"). Turning it off touches no app space.
- **Its open pages are told at once**: the chat hub hears `SuspensionChanged` (`HouseholdAgentsAnnouncer`)
  and sends `OnAccessChanged`; the fresh connection is refused and the page shows the notice. **And back
  again once resumed**: the chat client goes to the notice as `/accounts/account?returnUrl=<where it
  was>` (`AccountService.SendToNotice`), and the notice (`SessionCheck.Suspended`, `data-suspended`, `data-return` only for a
  return `ReturnUrls` accepts) asks for itself every 20 s (`PasskeyScript`) and goes back once no longer
  403.
- **Not stopped by it, deliberately**: the hub's per-turn session refresh still answers, so the agent
  answers `SuspendedAnswer` — a refused refresh would end the stream "signed out", the wrong words.
  `/accounts/me` still names the person. A refused fire counts as emitted, so the scheduler advances a cron and
  deletes a one-shot as if it ran: a one-shot due while suspended is gone on resume (kept so by the user, 2026-11-05); the Suspend confirmation and the members
  page say so.

## Tests

- `HouseholdListAdminTests`, `HouseholdSuspensionAdminTests`, `HouseholdAppsFeatureAdminTests` (seam 1; the agent's listing a Kestrel stub
  behind the shared secret).
- `HouseholdListE2ETests` on `DashboardAdminE2EFixture`: its stack is its own, so a test may create,
  change and suspend households freely. The base fixture keeps one page open at a time, so a second
  person uses `OwnerSessionAsync` / `OpenAsAsync` over HTTP rather than a page beside the operator's.
- Agent side: `ChatMonitorAssertionTests`, `ChatMonitorDelegationTests` over
  `MonitorTestMocks.Households(suspended, from)`; `TunnelRelayTests`.
- `StackSmokeTests.AHouseholdTheOperatorCreates_IsAnsweredByTheAgentItsOwnerNamed_UntilItIsSuspended`
  (`scripts/smoke-stack.sh`; key probes answered by the `smoke-keys` stub,
  `DockerCompose/docker-compose.override.smoke.yml`).
