# 09: Sharing approves asks

Spec: `.scratch/app-asks/spec.md` ("The approval covers asks").

**What to build:** An app's reach gains `asks`. When an owner shares an app as an app space, they see
and approve the servers its asks may use. A shared app's turns are offered only the approved servers.
A publish that asks beyond the approval says what waits on an owner, as reads do.

**Blocked by:** 05

**Status:** ready-for-agent

- [x] `AppReach` carries `asks`; `Within` and `Beyond` cover it.
- [x] The section's approved reach saves and reads it back, with shape checks.
- [x] The published-apps listing and the owner's sharing page show the app's asks, and the approval saves them (owner admin seam).
- [x] On a shared app the message carries only the approved servers, for every viewer, the author too (origin seam).
- [x] An unshared app's author gets the whole manifest.
- [x] `./publish` of a shared app asking new servers says they wait on an owner, and the publish stands.

## Comments

**Done (2026-11-26).** What 10-11 build on:

- **`AppReach.Asks`** (server names, as `AppManifest.Asks` names them): `Of`, `Within`, `Beyond`, `IsEmpty`,
  `Equals`/`GetHashCode` and `Problems` cover it (`'<name>' is no server's name`, judged by
  `TunnelCredentialProtocol.IsServerName`, the manifest's rule). The `spaces` section's `approved.asks` is
  additive, no schema step (as `approved` itself): a section saved without it reads as **no server approved**,
  so every space shared before this ticket keeps offering its asks none until an owner approves (fail closed,
  intended). A document written with `asks` is refused by older code (strict read), as `approved` was.
- **Listing**: `PublishedApp.Asks` and `AdminSpaceApp.Asks` are init properties outside the positional
  constructor, defaulting to none, so an answer from a side that predates them reads as asking none rather
  than null. Each has a `Reach` (`[JsonIgnore]`), the one place a reach is built from a listing
  (`HouseholdSpacesAdmin.Approved` caps with it).
- **Owner admin**: the approval saves the servers only as the page sent, capped `Within` what the app asks now,
  in the app's order (a server the app does not ask is dropped); merged per field inside `approved` like reads
  and connect (another owner's connect-only approval keeps mine's servers). `PendingOf` includes asks.
- **UI** (`AppSharedFacts`): a line "Its asks may use ..." (to share) / "Its asks are approved to use ..."
  (shared), "no server" when none; the pending sentence gains "use X for its asks". "Share as a space" and
  "Approve what it asks" approve `app.Reach`, servers included. There is no bUnit in `Tests`, so the
  component has no test of its own: its logic is pinned through `SpaceDraftsTests` and the admin seam.
- **Origin**: `AppPages.Granted` narrows `Asks` `Within` the approval (the `Asks = []` stopgap is gone), so
  `AppTurn.Servers` is the served manifest's asks the approval names, in the manifest's order, for every
  viewer, the author too; an approval naming no servers grants none; an unshared app's author gets the whole
  manifest. Unchanged existing rule: an author whose spaces are unreadable or refused gets the whole
  manifest's asks, as their reads (their own data only).
- **Publish**: the "waits on an owner" sentence names servers too ("its asks using vault"), and the
  could-not-be-checked sentence says "reads, talks to and asks with"; the publish stands.
- **Left for 11**: the `apps` skill still says an app space is granted "the reads and origins an owner approved"
  and that an ask has no tools; widening that line put the skill over its budget, so 11 says servers are
  approved too when it teaches asks.
- **Tests**: `AppReachTests` (new), `HouseholdSpacesProblemsTests` (round trip, an old section reads none,
  a dotted/ported name refused), `AppsAsksTests` (`ASharedAppsAsks_AreGrantedOnlyTheServersAnOwnerApproved_ForEveryViewer`
  replaces the stopgap's test: Her and Him get `["sugary"]` from asks `["vault","sugary"]` approved
  `["homeassistant","sugary"]`, none once the approval names no servers; `AnUnsharedAppsAsks_AreGrantedTheWholeManifest`),
  `AppsPublishedListingTests` (served commit's asks, not the tree's), `HouseholdSpacesAdminTests` (pending and
  approved asks in the wider-publish case; `ApprovedServers_AreWhatThePageSent_CappedAtWhatTheAppAsks_AndMergedWithAnotherOwnersApproval`),
  `AppsSpacesTests` (publish names a waiting server, not an approved one), `SpaceDraftsTests`.

