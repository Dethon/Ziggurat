# 08: Owners set the cap

Spec: `.scratch/app-asks/spec.md` ("The cap").

**What to build:** A household owner sets a household default cap and a per-app override on their
apps admin page. The values live in the household configuration and are read by the apps server
through a view, as `spaces` is. The limit an ask meets is the app's override, else the household's
default, else the deployment's.

**Blocked by:** 07

**Status:** done

- [x] A new part of the household configuration holds the default and the per-app overrides, saved by owners only, merging per item against what the page loaded (owner admin seam).
- [x] The values are bounded: no negative cap. A per-app entry must name a published app of the household.
- [x] The apps server reads the view on the viewer's assertion. A save is heard and re-read within the view's keep time.
- [x] If the configuration cannot be read, asks are refused `transient_dependency`. The cap is never lifted (origin seam).
- [x] The resolution order (override, household default, deployment default) is tested end to end (origin seam).

## Comments

**Done (2026-11-26).** What 10-11 build on:

- **The section** `app-asks` (`HouseholdSections.AppAsks`, `AppAsksSection { dailyCap?, apps: { <app id>: cap } }`),
  a sixth section, unversioned first shape, no secret. Its rules (held on the operator's
  `set-household-config` too): each cap 0-`AppAsksSection.MaxDailyCap` (10,000), each key a `BuiltAppId`.
  `AppAsksSection.LimitOf(appId, deployment)` is the one resolution: the app's own, else `dailyCap`, else
  the deployment's `Asks:DailyCap`. None saved reads as no caps (so the deployment's 200), no migration or
  seeding needed. A cap of 0 refuses every ask (429 with no `freesAt`).
- **The view** `app-asks` (`HouseholdConfigProtocol.AppAsksView`), `mcp-apps` alone (`InternalCallers`), on the
  call token or the `apps` assertion (`proofs.SharedAsync`, as `spaces`). `IHouseholdConfiguration.AppAsksAsync`
  (a default answering none, as Sugary's views do, for the readers that are not the apps server's) and
  `ICallHousehold.AppAsksAsync`; the client keeps it per household a minute or until any save of the household
  is heard, as every view.
- **`AskCaps.LimitAsync(app, ICallHousehold, ct)`** now takes the household to read on and answers an
  `AskLimit` (`Read(limit)` / `Unreadable` / `Refused`, the `AppSharing` catch filter, the reader's own timeout
  unreadable). `AgentAsks` hands it `CallHousehold.OfPerson(app.HouseholdId, assertion, households)`; **ticket
  10 hands it the check's call-token household** (`call.Household`) instead. Unreadable is 503
  `transient_dependency` (`AppAsks.CapUnreadable`), refused 422 `permission_denied` (`AppAsks.CapRefused`), both
  before counting or sending: the deployment's value is never used in their place.
- **Owner admin**: `GET/PUT /accounts/admin/household/app-asks` (`AdminProtocol.AppAsks`, `HouseholdAppAsksAdmin`)
  answers `AdminAppAsks { caps, apps }` (`AdminCappedApp`: id, name, colour, author's name now, link; apps
  null when the apps server could not be asked, none without the feature), merged per app id through
  `EditMerge` (an app's cap removed on one page comes out of the merge as `null`, dropped before the strict
  read). A cap **set or changed** must name a published app of the household (400 naming it); 503
  `AdminSpaces.AppsUnavailableMessage` when the listing is unavailable; the household default and removals
  save without it, and a cap left as loaded (a deleted app's) never blocks a save.
  `HouseholdSpacesAdmin.PublishedAsync` is shared (internal). Summaries name app ids, never values.
- **UI**: a new `/admin/apps` page ("Apps", after Spaces in `AdminArea.Pages`): "Asks per day, each app, whole
  household" (empty: the deployment's), then each published app's "Asks per day, this app, whole household"
  (empty: the household's), then any cap of an app no longer listed, by id, disabled with "Remove its limit".
  Number inputs, no icons. Logic in `AppCapDrafts` (unit-tested); store `HouseholdAppAsksStore`, effect
  `HouseholdAppAsksEffect`, `IAdminService.GetAppAsksAsync`/`SaveAppAsksAsync`.
- **Not shown**: the deployment's own value (`Asks:DailyCap` is the apps server's; the accounts service does
  not know it), so the page says "the deployment's limit" rather than a number.
- **Tests**: `HouseholdAppAsksProblemsTests` (bounds, ids, resolution), `HouseholdConfigurationTests`
  (`TheAppsServer_ReadsTheHouseholdsAppAsks_OnTheCallTokenAndItsOwnAssertion`), `HouseholdAppAsksAdminTests`
  (owner saves and the apps server's reader hears it, member 403, bounds, unknown app, listing unavailable,
  two owners merging per app and a 409 for one app capped two ways), `AppsAsksTests`
  (`TheLimit_IsTheAppsOwnCap_ElseItsHouseholds_ElseTheDeployments`,
  `CapsThatCannotBeRead_RefuseTheAsk_SendingAndCountingNothing` for down and refused),
  `AppAskCapsOverTheWireTests` (real accounts + Redis, both servers on a standing clock: the household's
  default answers, a saved app cap is heard and read again, then caps), `AppCapDraftsTests`,
  `AdminServiceEditTests.SavingAppCaps_SendsTheCapsThePageLoaded`, `HouseholdAppsE2ETests` (saves in the
  stack's second household). The fixture's `HouseholdConfigFromAccounts` (was `AppSpacesOverTheWireTests`'
  private `SpacesFromAccounts`) forwards spaces and app asks to the real reader; `AppsServer.AskCaps` is the
  stand-in.
