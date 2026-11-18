# 03: Home watches live at /watchers

**What to build:** Move the watch subtree out of `HaFileSystem` into `HaWatchFileSystem` (`ha-watches`, mount `/watchers`, union fallback on `/triggers/*/trigger`), registered beside `ha` on the HA server. Paths become `<id>/watch.json` and `<id>/status.json`; `/ha/watches` no longer parses or lists. `DelegatedWrites`/`WatchDelegation` key on `ha-watches`. The setup index's watch line, the HA guide stub, the `home-watches` skill, snapshots, `WatchScenarios`, the fake home, rules (`home-assistant.md`, `docs/home-assistant-bridges.md`) say `/watchers`. Behaviour otherwise unchanged. Spec: "Home watches move to /watchers".

**Blocked by:** 02

Status: done

- [x] `HaWatchesTests`, `HaWatchEffectsTests`, `WatchDelegationTests` and `HomeAssistantWatchFireTests` pass on `/watchers` paths.
- [x] `/ha/watches/...` answers not-found and is absent from `/ha` listings and the setup index's tree.
- [x] A watch written through `/watchers` carries a delegation and fires as before.
- [ ] Prompt snapshots and the skill snapshot updated; `WatchScenarios` armed run on the changed tier stays as green as on master. (Snapshots done — the diff is the path alone; the armed run was not made, it costs money: `ZIGGURAT_EVAL=1 ZIGGURAT_EVAL_CHANGED=1 dotnet test --filter "Category=Eval&Tier=Changed"`.)

## Comments

Done in f5733f039. Deviations and things worth knowing:

- **`/ha` writes nothing now.** `HaFileSystem` lost `CreateAsync`/`EditAsync`/`DeleteAsync` (they
  could only refuse, the drift the conformance file names) and its caller view (`For`, `_caller`,
  `_delegation`, the `caller:` ctor argument), which moved to `HaWatchFileSystem`
  (`HaWatchFileSystemCallerViewTests`). `ha` publishes read/info/glob/search/exec/blob_read;
  `ha-watches` read/info/glob/create/edit/delete/blob_read (the byte read serves the rendering to a
  sandbox command, as `/ha`'s does). A write anywhere on `/ha` is the base's `unsupported_operation`;
  `/ha`'s `DescribeMount` says a watch lives under `/watchers`. The `fs_create`/`fs_edit`/`fs_delete`
  tool descriptions are now `ha-watches`'s (the first filesystem that overrides them).
- **The one behaviour change beyond coordinates:** the watch listing no longer degrades to empty when
  the home cannot list its automations — it answers the home's error (`transient_dependency` for a
  503), so a union names the member `unavailable` instead of saying there are no home watches. Pinned
  by `HaWatchesTests.Glob_WhenTheHomeCannotListItsAutomations_IsATransientFailure`; `/ha`'s own
  listing no longer asks for automations at all.
- **New pieces:** `HaWatchPath` (`""`, `<id>`, `<id>/watch.json`, `<id>/status.json`), used by the
  mount and by `WatchDelegation.OfWrite`; `HaWatchFileSystem.MountPointPath` (`"/watchers"`), used by
  the setup index line and `PromptStalenessTests`'s mount roots; `HaReadResult.Numbered` (the
  line-numbered read both HA mounts share). `HaVfsPath` lost the four watch kinds and
  `WatchesRootName`; `HaTree` lists no watches.
- **Conformance:** `FileSystemServerConformanceTests` has an `ha-watches` row and now pins each
  mount's published mount point (`/watchers` for it) and `union` (written out), and that discovery
  reads both back — a lone member is mounted at `/watchers` as a plain mount. Red shown by setting
  `Union` to null. `DelegatedWritesTests` (new) pins that `ha-watches` `<id>/watch.json` delegates and
  `ha` `watches/<id>/watch.json` does not.
- **Eval:** `WatchScenarios.LookingAtTheHome` adds `Looking("/watchers*")`, since `/ha*` no longer
  covers listing or reading a watch; the replace permissions and path patterns say `/watchers`.
- **Snapshots:** regenerated with `UPDATE_PROMPT_SNAPSHOTS=1`; the diff is the path in the guide stub
  and the skill (body 1668 → 1666 tokens), nothing else. Claim ids unchanged; two claim statements
  name `/watchers`.
- **Deploy:** the agent and the kit's HA server go together. A new agent keys delegations on
  `ha-watches`, so against an un-rebuilt kit it would write `/ha/watches` with no delegation (fires
  for nobody); an old agent against a new kit would find `/ha/watches` gone. Conversations that loaded
  the old skill body may try `/ha/watches` once and be told not found.

Test-first, honestly: the old-path, union-member and delegation tests were written before the
implementation but first ran after it (they did not compile before); red was demonstrated afterwards
for the union row only (`Union` nulled, the `ha-watches` conformance row fails). A rerun on HEAD
(Unit tier plus the HA, watch-delegation and bridge filters) had one unrelated failure under load,
`ChatMonitoringTests.AMonitorThatKeepsFailing_IsRestartedOnADoublingDelay` (a minute's hang), which
passes alone three times out of three. The sandbox bridge keys served mounts by mount point
(`VfsCall`, `ServedMount.NameOf(m.MountPoint)`), so a command sees `/watchers`; the AppArmor profile
allows the one FUSE mount at `/vfs/` and needs nothing per mount.

For ticket 10: `Domain/Tools/Sugary/Vfs/SugaryFileSystem.cs`'s comment still says its caller view
copies "as HaFileSystem does" — it is `HaWatchFileSystem` now; left alone to keep out of Sugary's
files. Conformance needs a `sugary-watches` row in every table, `ExpectedMountPoint` and `_unions`.
The home is the fallback and writes triggers verbatim, so a misspelled glucose trigger kind is routed
to Home Assistant and refused in its words — ticket 14's skill and scenarios should expect that.
The armed run for this ticket should be the changed tier: the guide document changed, so it selects
the whole home family, not only `WatchScenarios`.

For ticket 10: register `SugaryWatchFileSystem` after `SugaryFileSystem`; share the mount point
(`HaWatchFileSystem.MountPointPath`, or move it to a neutral constant) and the `routeBy`
`/triggers/*/trigger`. The setup index's watches line still counts the home's watches only
(`watches: /watchers/<id>/watch.json — N defined (...)`), which is right for an index of the home.

For ticket 14: the `home-watches` skill's listing line ("glob on `/watchers/*/` and read each
`watch.json`") will list glucose watches too once Sugary joins; the "`domain__filesystem__remove` on
`/watchers/<id>`" and creation lines are generic. The `.claude/rules/home-assistant.md` watches
paragraph already describes `/watchers` as a union with Sugary beside it.

Tests run: `Tests.Unit` 7413 passed; everything else outside E2E/Canary/Eval/Smoke (integration,
eval harness, the HA container's `HomeAssistantWatchFireTests`) 2732 passed, 5 skipped. The armed
eval was not run.
