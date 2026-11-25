# 09: App spaces

Spec: `.scratch/app-spaces/spec.md` ("Spaces and WebChat").

**What to build:** An owner shares a member's published app with the whole household. `HouseholdSpace` gains an optional built-app id. The spaces admin page lets an owner add an app space by picking one of the household's published apps, after seeing its author, `reads` and `connect`. Removing the space unshares the app, which stays its author's. While an app is an app space, every member of the household who is not disabled may open its published origin.

**Blocked by:** 04

**Status:** ready-for-agent

- [x] The admin page asks the apps server for the household's published apps, and shows author, mounts and origins before adding one.
- [x] An app space's name and colour are the app's. It holds no topics, and `JoinSpace` on it answers "No such space."
- [x] While shared, other members open the published origin, a disabled member gets a 404, and another household's member gets a 404.
- [x] Removing the app space unshares the app: only its author can open it again.
- [x] Deleting an app that is an app space is refused and names the space.
- [x] An owner orders app spaces among chat spaces.
- [x] Covered by spaces admin integration tests and apps server access tests.

## Comments

**Landed (branch `app-spaces`).** `HouseholdSpace.AppId` (plain optional field, as `FeaturesSection.Apps` was
added; no schema step). Apps server: `AppPages.AccessAsync` (the one rule for pages, reads and reports, which
replaced `MayOpen`), `AppsFileSystem.SharedRefusalAsync`, `Serving/PublishedAppsListing`,
`BuiltAppStore.PublishedManifestAsync`. Wire: `Domain/Apps/PublishedApps`, `AppChannels.PublishedApps`.
Accounts: `Admin/HouseholdAppsClient`, `HouseholdSpacesAdmin` (listing, stamping, kind rules),
`InternalCallers` (`mcp-apps` reads `spaces`; `apps` is its assertion channel), `AccountsSettings.AppsUrl`/
`AppsTimeout`, compose `APPSURL`. Hub: `SpaceListing.ChatSpaces` in `ChatHub.HouseholdSpacesAsync`. Client:
`SpaceDrafts.Share`/`Shareable`, `AppSharedFacts`, the spaces page. `ICallHousehold.SpacesAsync`. Contracts in
`apps.md` ("App spaces"), `admin-owner-pages.md`, `household-config.md`, `chat-hub.md`, `person-assertions.md`,
`accounts.md`.

Tests:
- `AppsSpacesTests` (5, stand-in spaces `AppsServer.Spaces`): a member opens a shared app's page and reports;
  the preview stays the author's; another household's member gets the one 404 even when both households name
  the id; unsharing closes it; unreadable spaces are a 503 for a member and nothing to the author.
- `AppSpacesOverTheWireTests` (real accounts and Redis): an owner reads the listing through the admin API and
  shares the app; a member signed in through the app sign-in opens it; another household's member gets 404; a
  member disabled through the admin API gets 404 once `SessionsEnded` ends their app session; removing the
  space closes it to the member and leaves it to the author.
- `AppsPublishedListingTests` (3): what the listing answers, the assertions it refuses (member, `apps`,
  `signalr`, forged, none), and that the pages listener does not answer it.
- `McpAppsServerTests`: delete refused naming the space and allowed once unshared; unreadable spaces delete
  nothing.
- `HouseholdSpacesAdminTests` (+12 with a real apps server): the listing with author name, reads and connect;
  no apps without the feature; name and colour stamped from the app and order among chat spaces, with the
  summary; unpublished, another household's and unknown apps refused; five kind changes refused; an apps
  server down stops only a new app space.
- `HouseholdConfigurationTests`: the apps server reads `spaces` on a call token and its `apps` assertion, and
  is refused with a `signalr` one.
- `HouseholdSpacesProblemsTests` (5, the section's rules); `ChatHubSpacesTests` (+3: `GetSpaces` without app
  spaces, `JoinSpace` refused, topics under an app space's slug listed in the default space); `SpaceDraftsTests`
  (+4); `DeploymentConformanceTests.TheAccountsService_AsksTheAppsServersListingOnItsMcpListener`.

**Red first:** the hub, delete, access (3 of 5), listing (2 of 3), admin, views-table, client and conformance
cases were seen failing before the code. Passed on arrival and proven by breaking the code: the preview and
other-household access cases (dropping the preview/household check failed both), the section rules (dropping
each rule failed its case), and the over-the-wire test (taking `apps` out of the assertion channels timed it out
at "the shared app to open"). The pages-listener listing case passes because `AppPagesListener` runs first; it
was not broken on purpose.

**Choices the spec left open:**
- *One source of truth*: the household's `spaces` section. The apps server keeps no copy. It reads the view
  through `HouseholdConfigurationClient` (kept a minute, dropped on a heard save), on the viewer's `apps`
  assertion for an origin and on the call token for a delete. An author's own app never waits on accounts.
- *`GetSpaces` filters app spaces now* (chat spaces alone, as ticket 10 requires), and so do `JoinSpace`,
  `DefaultOf`, `ListedWith` and `ShownIn`. A slug an app space took that still holds a removed chat space's
  topics lists them in the default space.
- *The listing's credential* follows the usage route's pattern: the accounts service signs an assertion for the
  gate-verified owner on a new channel, `published-apps`, which no proxy block names. The household is its
  `tid`; `owner` is required. It is served on the MCP listener outside `/mcp`, so the MCP secret does not gate
  it. `apps-browser` can reach that listener but holds no such assertion. No new secret.
- *Name and colour* are stamped from the apps server's answer on every save and on every admin read. When the
  server is down they stay as last saved. The page's word is never taken. The stored copy keeps the section's
  rules (a name, a colour) satisfied.
- *What a space is never changes under its slug.* An app space keeps the app it was added with, and a chat
  space never becomes an app space, or its conversations would sit in a space nobody joins. One space per app.
  An app space opens on no agent. The default space is never an app space. The section enforces the last
  three, so the operator's command is held to them too.
- *The apps server is asked only for a household with the `apps` feature*: no feature, no apps offered. An
  apps server down gives `Apps = null` (the page says so), a 503 only for a save that adds an app space, and
  any other save stands.
- *Outage vs refusal*: spaces that cannot be read are a 503 (a page, a read's `transient_dependency` envelope,
  a report). A refusal from the accounts service counts as closed.
- *The new app space's slug*: the app's slug, else the first free `<slug>-<n>`, set by the page. The section's
  slug rule applies.

**Disabled members:** nothing in the section names them. The 404 comes from the session ending: disabling
revokes their sessions, `SessionsEnded` ends their app sessions (`EndAll`), and a renewal answers nobody.
When the announcement is missed, it arrives within `Sessions:AssertionKept` (5 min) at worst.

**Known gaps:**
- A later publish can widen `reads` or `connect` after the owner approved them. The owner sees only what the
  served commit says now.
- Without Redis, unsharing and the delete check lag by up to `Accounts:ConfigurationLifetime` (1 min).
- `AppSpacesOverTheWireTests` does not cover the delete refusal: the suite's call tokens are signed for
  `localhost`, which the real accounts service does not take as `mcp-apps`. The refusal is covered against the
  stand-in, and the view's call-token row by `HouseholdConfigurationTests`.
- The spaces page was built and its drafts unit-tested, but not opened in a browser. No E2E (ticket 10).

**Run:** `~Apps`, `~Spaces`, `~Conformance`, `~HouseholdConfigurationTests` and the touched classes. Final run:
1027 passed and 2 skipped of 1029. The first such run after a fresh build had one failure:
`AppsServingInABrowserTests.APublishedAppThatThrows_HasItsErrorsReportedToItsErrorsFile_OnceEach`, after 710 ms,
with no message captured. Six reruns, alone and in the full filter, all passed. That case is the author's own
session, which never reads the spaces. It is still a possible load flake to look at. The `~Apps` filter also
matches `WebChatSignInE2ETests.ALinkToAnotherServicesPage_…`, which timed out once while the stack was
starting and passed after.

**Not run:** the full suite, E2E on purpose, smoke and eval. No image was built.

**Deploying this needs:** no new secret. `APPSURL: http://mcp-apps:8080` on `accounts` ships in compose.
- Rebuild `base-sdk` first: Domain, Mcp.Hosting and Infrastructure changed.
- Then rebuild `accounts`, `mcp-apps`, `mcp-channel-signalr` and `webui` together. Until the hub runs this
  build, an older hub would show a saved app space as a chat space it cannot open.
- An accounts service older than this build reads its spaces section strictly and refuses a saved `appId`.
  Once an app space is saved, rolling `accounts` back makes that household's spaces unreadable until the
  space is removed by hand.

