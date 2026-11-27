# 10: WebChat space switcher lists apps

Spec: `.scratch/app-spaces/spec.md` ("Spaces and WebChat"); Testing Decisions, seam 4.

**What to build:** A person finds their apps where they find their spaces. A new hub method answers chat spaces, app spaces with their URLs, and the person's own published apps that aren't app spaces, marked private. The hub gets the person's apps from the apps server. The switcher uses the new method, and picking an app opens its origin. `GetSpaces` keeps its old answer for installed clients from before.

**Blocked by:** 06, 09

**Status:** ready-for-agent

- [x] The switcher shows app spaces among chat spaces in the household's order, and the person's private apps marked private.
- [x] Picking an app opens its origin. The app's icon is drawn, never typed.
- [x] `GetSpaces` answers exactly what it did before.
- [x] An E2E collection of its own, with the apps origin on a local wildcard name: a signed-in member sees an app space, opens it, is signed in through the app sign-in, and the app renders data read through `/_vfs/*`. A second household's member gets a 404.

## Comments

**Landed (branch `app-spaces`).** Wire: `Domain/Apps/PersonsApps` (`/internal/persons-apps?shared=<id>…`,
`PersonsApp`, `PersonsAppList`), channel `AppChannels.PersonsApps` (`persons-apps`). Apps server:
`Serving/PersonsAppsListing` on the MCP listener. Accounts: `InternalCallers` lets `mcp-channel-signalr` name
`persons-apps` on the session refresh (`SessionRefresh._refreshed`), nobody else. Hub: `IPersonsApps`
(Domain contract), `Infrastructure/Clients/Apps/PersonsAppsClient` (session refresh on `persons-apps`, then the
listing; `AccountsResolverClient.SessionOnAsync`), `ChannelSettings.AppsUrl`/`AppsTimeout` (compose `APPSURL:
http://mcp-apps:8080` on `mcp-channel-signalr`, appsettings 3 s), `ChatHub.GetSpacesAndApps` over
`Domain/DTOs/WebChat/SpaceSwitcher` (`SwitcherEntry`). Client: `ITopicService.GetSpacesAndAppsAsync`,
`SpaceState.Switcher`, `LoadSwitcher`/`SwitcherLoaded`, `SpaceEffect.LoadSwitcherAsync`, the new
`Components/SpaceSwitcher.razor` in the header, `Icons.Apps`. Contracts in `apps.md` ("A person's apps"),
`chat-hub.md` (Spaces), `person-assertions.md`, `channel-identity.md`, `testing.md` (a fixture's own browser).

Tests:
- `AppsPersonsAppsTests` (4): own published apps and the named shared ones, nothing unpublished, nobody else's
  private app, nothing of another household; an own shared app listed once; refused on `apps`, `signalr`,
  `published-apps`, the guest, forged, none; not on the pages listener.
- `PersonsAppsClientTests` (4, real accounts service and apps server): a live session's apps; a revoked
  session, an apps server that does not answer and no `APPSURL` are no apps.
- `SessionRefreshTests` (+1): the hub naming `persons-apps` is answered on it; Sugary's credential is 403; the
  hub's unnamed ask stays on `signalr`.
- `ChatHubSpacesTests` (+4): the switcher's order (chat and app spaces by the section's order, then private
  apps by name), the ids and session asked; an own shared app is its space alone; apps not asked leave the chat
  spaces; `GetSpaces`' JSON byte for byte with apps answered.
- `SpaceStoreTests` (+1), `SpaceEffectTests` (+2), `InitializationEffectTests` (the first load's order): the
  list kept across space changes and replaced by the next answer; asked on becoming live again and on
  `LoadSwitcher`; not live keeps the last list silently; the first load dispatches `LoadSwitcher` after the
  topics' first page.
- `McpServerContractTests.EveryServiceAChatHubMethodTakes_IsRegisteredByTheChannelsModule` (the composition:
  the real `ConfigureChannel`), `DeploymentConformanceTests.TheChatHub_AsksTheAppsServersListingOfAPersonsAppsOnItsMcpListener`.
- `AppSpacesE2ETests` (collection `WebChatE2E.AppSpaces`, slice `zulu`, `CollectionSlices` 25): an owner of
  the second household publishes an app reading `/vault` (built over `mcp-apps`' `/mcp` as the app reader
  agent, its note written over the vault's) and shares it through the spaces API; a member's own app is
  published unshared; the member opens WebChat, the switcher lists `Main`, the app space, then their app
  marked Private; picking the app lands on its origin through the app sign-in and the page shows the note read
  through `/_vfs/read`; a home member opening the link gets 404 on the app's host. Wall time warm: image check
  0.2 s, stack 4.9 s, the case 3 s (about 10 s); first run after a `webui` rebuild 25 s of image build more.

**Red first:** the listing (3 of 4; the pages-listener case passed on arrival, as ticket 09's did), the session
refresh case, the hub's three switcher cases ("Method does not exist"), the client's live-session case against
a null stub, the conformance case, the store and effect cases (3), the load-point move (the first load's order
and the live-again case), and the E2E at the switcher's button (after fixing the test's own vault path). The
composition case was seen red by taking `AddPersonsApps` out. The client's three null cases passed against the
null stub and were each seen red by breaking the client (no empty-URL check, a revoked session not stopped, an
unreachable apps server not caught). The `GetSpaces` case pins the hub's JSON as a literal and guards what
did not change; it passed on arrival.

**Choices and deviations:**
- *There was no space switcher*: spaces were reached by address alone. The header's mark is now the switcher
  (a button with the mark and a caret, "Spaces"), a plain mark when there is one place. A chat space is an
  in-app link to its address; an app is a plain link to its origin (a top-level navigation, same tab).
  `GetSpaces` stays what validation and joining use.
- *When the list is asked*: last in the first load (after the agents and the topics' first page), on becoming
  live again, and whenever the switcher opens. SignalR answers a connection's invocations one at a time, and
  this one costs two accounts calls and the listing (up to `AppsTimeout` with a hung apps server), so it is
  never in front of the chat's own calls; a space switch does not ask (the list is the household's). A
  household with one place shows a plain mark, so a first app published or shared since the page loaded
  appears after the next reconnect or reload, not live.
- *The hub method takes `IPersonsApps` as `[FromServices]`* (as `DismissAlert` does), not by constructor: some
  sixteen test hosts build the hub by hand. The composition test above holds the module to registering it.
- *The listing reads no spaces.* The hub names the app ids its own spaces read found, and the apps server
  answers those published in the assertion's household with name and link only. So a holder of a
  `persons-apps` assertion (the hub alone) can learn an unshared app's name and link by its id; giving the
  listing the `spaces` view on a second assertion channel would close that and cost a read per call.
- *An app space whose link cannot be had is left out* (the apps server down, slow, unset, or the session
  gone): the switcher shows the chat spaces. The apps feature is not consulted: a household whose apps feature
  is off still lists apps already published.
- *The E2E's app lives in the stack's second household*, and the 404 is a home member's
  (`CreateSignedInPageAsync`), not `PersonElsewhereAsync`: the reading agent has to be one of the household's
  with a vault whitelist (`test-agent` asks every tool; `sugary` has none), and a fourth agent in the home
  would restack the picker every collection reads. The second household got `app-reader` and the apps feature
  in the seeded store.
- *The apps domain in the E2E is https on Caddy's local certificate* (`*.apps.localhost`, `tls internal`,
  published at a picked port), mirroring the deployment's site, since an app's link is `https://` with no port.
  The collection launches its own Chromium (`E2EFixtureBase.OwnBrowserArgs`) with
  `--host-resolver-rules=MAP *.apps.localhost 127.0.0.1:<port>`; contexts already ignore certificate errors.
  Whether the app's service worker registers on that certificate was not checked.
- *The stack's vault is a tmpfs* now, so a test can write the note an app reads; `mcp-apps` runs without
  `apps-browser` (no check runs).
- *Fixed on the way:* `HouseholdSpacesE2ETests.AnOwner_IsShownTheHouseholdsFeatures` looked for a checkbox
  named exactly "Apps", but its name carries the hint; it had never run since ticket 11.

**Run:** the filter `(~Apps|~Spaces|~Conformance|~ChatHub|~SessionRefresh|~McpServerContractTests|
~AccountsResolverClient|~InternalCallers|~WebChat.Client|…)&FullyQualifiedName!~E2E`: 2170 passed, 2 skipped.
`WebChatCollectionSliceTests`, `E2EImagesTests`, `DrawnIconConformanceTests` passed. E2E: `AppSpacesE2ETests`
(three runs), `HouseholdSpacesE2ETests`, `HouseholdAdminE2ETests`, `HouseholdMembersE2ETests`,
`SeesAllWithdrawnE2ETests`, the Hearth classes and `WebChatPageE2ETests` (the header changed): all passed after
the fix above; after moving the load points, `WebChatApprovalE2ETests`, the picker and space cases and this
collection again. Not run: the full suite, the other E2E collections, smoke, eval. The switcher was looked at
once in screenshots taken by a throwaway edit of the E2E (desktop with the app space and the private app; a
phone with the home's 26 spaces, the list scrolling inside 60% of the screen); nothing kept.

**Deploying this needs:** no new secret, no Caddy change. Rebuild `base-sdk` first (Domain and Infrastructure
changed), then `accounts`, `mcp-apps`, `mcp-channel-signalr` and `webui`; `docker-compose.yml` gains
`APPSURL` on `mcp-channel-signalr`. Until `accounts` runs this build the hub's ask is refused (403) and the
switcher shows chat spaces only. An installed WebChat keeps its old client until its worker updates, and
keeps working on `GetSpaces`.

