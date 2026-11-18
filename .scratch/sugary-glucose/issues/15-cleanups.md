# 15: Loose ends found while building 01–13

**What to build:**
1. **Sugary turned off does nothing for that household** (decided 2026-11-18): the poller polls no link, the engine judges no watch, no prompt or alarm fires, for a household whose features turn Sugary off; the app is already refused. Sugary learns the features from the household's settings view (the way it already reads settings; a minute's keep, like the others). A household switching Sugary back on resumes at the next poll; watches' states are not advanced while off (a crossing while off does not fire later).
2. **One folder rule.** `SugaryPeopleFolders` duplicates `SugaryPath.FolderOf`'s rule; fold into one and drop the pinning test.
3. **One id across union members.** A create routed to one member must be refused when another member holds that id even if the caller cannot see it there (a non-grantee writing a home watch under an id a hidden glucose watch holds): the union asks the other members whether the entry exists at all, and a member that holds a hidden entry answers so without revealing it. Generic in `UnionFileSystem`; a member contract, not watch code.
4. **No household ids or LAN addresses in tracked tickets**: replace them in ticket 09's Comments with placeholders.

**Blocked by:** 13

Status: done

- [x] Sugary off: no poll, no judgement, no fire; back on: resumes, nothing late.
- [x] One folder implementation.
- [x] A hidden id refuses a create routed to another member, with a message that reveals nothing about the hidden entry.
- [x] Ticket 09 carries no real ids.

## Comments

Done in 7bbc7c2b8 (folders), 7f6f3598e (ticket 09), d44574998 and baf0e520f (one id across members),
e3b81d3cd and 3ba6e5cdc (Sugary off).

**1. Sugary off** (e3b81d3cd)

- **Where the features come from: a new view, not the settings view.** The settings view is proven by a call
  token or an assertion, and the poller and the engine hold neither (no request, no call). So the accounts
  service answers `mcp-sugary` a view across households, `sugary-off` (`HouseholdConfigProtocol.SugaryOffView`,
  granted in `InternalCallers`' view table, 403 to every other caller): `HouseholdsWithSugaryOff`, the ids of the
  households whose *saved* features turn Sugary off and nothing else (one that never saved its features has it
  on, its default). It takes no proof, like `telegram-bots` — `.claude/rules/household-config.md` names it
  "Exception 3". The reader (`IHouseholdConfiguration.HouseholdsWithSugaryOffAsync`, default empty for every other
  reader) keeps it as one entry; any household's `features` save drops it where there is Redis (Sugary has none,
  so a minute).
- **`SugaryOffHouseholds`** (`McpServerSugary/Glucose/`) is what the poller and the engine ask. The poller leaves
  an off household's links out before it keeps their pace, so back on a link is seen again at the next sweep and
  asked at its own moment (within a period, as a new link is). The engine checks inside each judgement (poll
  and clock alike), so nothing is judged and nothing fires.
- **Fail open.** When the view cannot be read (outage, refusal, an accounts service older than this change
  answering 404 — any exception but our own cancellation), every household is taken as on: polling and judging
  go on, because a medical alarm must not go silent because the accounts service blinked. Logged once per outage
  (a warning, then an information line on recovery); the failure is remembered for a minute so a hung service is
  not asked on every five-second sweep, each sweep waiting out its timeout. The cost: for up to a minute after a
  failure, an off household is polled and judged.
- **"States are not advanced while off" became "states are forgotten while off".** Leaving the rows alone made
  a low still going at resume fire at once (the stored state said "not holding", so the first poll back on read
  as a crossing) — a crossing from while off, fired late. So while a household is positively known off, the
  engine deletes what it judged of its subjects' watches (`GlucoseWatchStates`), and back on they are judged as if
  just written: a trigger holding then is recorded fired without firing, and the next crossing fires. Only a real
  "off" answer deletes; an unreadable one never does. A fire racing the delete writes nothing (the guarded save
  counts the rows it read). Consequence: `status.json` shows `holds: null` while off, and an `ending` already due
  at resume is not announced for that sensor.
- **Decision to confirm:** a low still going when Sugary is switched back on stays silent until the next
  crossing (the ticket's "nothing late" read strictly). Reversing it is dropping `ForgetAsync`: the stored
  "not holding" then makes the first poll back on fire.
- **Deploy order:** base-sdk and accounts before Sugary. A Sugary of this change against an older accounts service gets 404 for
  the view and fails open: it polls and judges everyone, warns once, and asks again each minute.
- **Not changed, for whoever decides:** `PenReminders` still pushes pen reminders to a household with Sugary off.
  The ticket names the poller, the engine and their fires only, the app still logs pens for such a household
  (only estimates and labels are refused), and whether a pen's replacement should still be reminded is a product
  call. `SugaryOffHouseholds` is there if it should stop.
- Tests: `HouseholdConfigurationTests.Sugary_IsToldWhichHouseholdsTurnItOff_AndNothingElse` and three rows in
  `AnotherServersViews`; `HouseholdConfigurationClientTests.Sugary_IsToldWhichHouseholdsTurnItOff_KeptForTheLifetimeOrUntilAFeaturesSave`
  (with and without Redis); `SugaryGlucosePollerTests` (off not polled, another household still is, back on
  polled again; unreadable keeps polling); `SugaryGlucoseWatchEngineTests` (off: no fire on a poll or the clock;
  back on with a low still going: nothing, then the next crossing fires; a low that came and went while off:
  nothing; another household off: hers fires; unreadable: fires). `SugaryServer.SugaryOff` switches a household
  off, on, or makes the answer fail (`EveryHouseholdIn`'s new `sugaryOff`). Red first: all compiled red or failed
  before the code; then by mutation — no poller filter, no engine gate, no forgetting, rethrowing on failure,
  and dropping only on a `keys` save each turned its test red. Follow-up (3ba6e5cdc): `SugaryOffHouseholdsTests`
  (unit: not asked again within the minute after a failure, asked after; an outage said once and its end once;
  its own cancellation rethrown) and `WhileOff_TheClocksPassForgetsWhatWasJudged_…` — in production only the
  clock's pass reaches an off household's watches, since the poller asks nothing; both red under mutation.

**2. One folder rule** (7bbc7c2b8): `SugaryPath.FoldersOf(people, readerUserId)` is the one rule; `SugaryFileSystem`
and `SugaryWatchFileSystem` call it, `SugaryPeopleFolders` and `SugaryPeopleFoldersTests` are gone.
`SugaryFileSystemPeopleTests`' twins case pins it (red with numbering switched off).

**3. One id across union members** (d44574998)

- **The member contract rides an existing wire operation**: `FsInfoResult.Hidden` (defaulted, off the wire while
  false, as `Executable` is) — "an entry is here that this caller may not see", answered with `Exists` false and
  nothing else. No new `fs_*` tool, no capability change, so `McpFileSystemBackend` carries it as it is.
- **`UnionFileSystem`**: before a new entry goes to a member — a create, a bridge write (both through
  `ForNewAsync`), or a move's or copy's destination nobody lists (`BetweenAsync`) — it asks every *other* member
  `InfoAsync("<entry>")` (keeping the caller's leading slash), and one answering `Exists` or `Hidden` refuses it
  `already_exists`: "'<id>' is taken: the id is already in use here." / "Choose another id." — naming no member
  and nothing of the entry. The target member answers for its own ids as before. A member that refuses or is
  away when asked contributes nothing (as a create while a member is away is routed by claims).
- **`SugaryWatchFileSystem.InfoAsync`** answers `Hidden` for an id `FindAsync` holds that the reader may not see
  (it did answer `Exists` false before, which is what let the home take the id). `HaWatchFileSystem` needs
  nothing: its listing hides nothing.
- Cost: one extra stat per other member on each create of a new entry (one for `/watchers` today).
- Follow-up (baf0e520f): a move or copy to the union's root asks nobody (the root stats as existing, which
  refused it as "'' is taken"); and a test beside the real `HaWatchFileSystem` (its `InfoAsync` answers an id it
  does not hold `Exists` false, spelled `low` or `/low`, so a glucose watch is created; an unreachable home
  throws, which the union counts as away).
- Tests: `UnionFileSystemTests` (6: refused saying nothing, refused with overwrite, a free id asks the others once,
  a member away does not stop it, a move onto a hidden id, a bridge write), `SugaryWatchFileSystemTests` (the
  stat, and a home watch under a hidden glucose id through the union), and
  `SugaryGlucoseWatchesTests.AHomeWatchUnderAnIdHerGlucoseWatchHolds_IsRefusedOverTheWire` — the union with
  Sugary's member behind `McpFileSystemBackend` against the real server, which is what proves it deployed.
  Red first; then each of dropping `Hidden` from the union's check and answering `Hidden = false` from Sugary
  turned them red. `.claude/rules/virtual-filesystem.md`'s union paragraph says it.

**4. Ticket 09** (7f6f3598e): the household id, the host name and the LAN address are placeholders; the id's
place is said (the dashboard's operator view, or `mint-link`'s `household <id>`). Nothing else under
`.scratch/sugary-glucose/` carried them.

Runs (at 3ba6e5cdc): `Tests.Unit` 7629/7629; the integration set below 1351/1351. Earlier, at e3b81d3cd: Integration — Sugary, accounts (`Tests.Integration.Accounts`), the server table
and contracts (`Tests.Integration.McpServers`), VFS bridge and tools, glucose-watch delegation,
`McpFileSystemBackend`: 1349/1350, the one failure `HouseholdListAdminTests.TheList_ShowsTheServersConnectedToTheAgentsRelay`,
which passed alone and in the whole accounts suite (490/490) twice — unrelated to this change, a load flake.
Sugary's E2E tests could not build `mcp-sugary` against the stale `base-sdk` image (the Domain change; build
base-sdk first) and were not run. Eval not run.
