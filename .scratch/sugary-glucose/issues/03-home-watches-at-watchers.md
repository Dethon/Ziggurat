# 03: Home watches live at /watchers

**What to build:** Move the watch subtree out of `HaFileSystem` into `HaWatchFileSystem` (`ha-watches`, mount `/watchers`, union fallback on `/triggers/*/trigger`), registered beside `ha` on the HA server. Paths become `<id>/watch.json` and `<id>/status.json`; `/ha/watches` no longer parses or lists. `DelegatedWrites`/`WatchDelegation` key on `ha-watches`. The setup index's watch line, the HA guide stub, the `home-watches` skill, snapshots, `WatchScenarios`, the fake home, rules (`home-assistant.md`, `docs/home-assistant-bridges.md`) say `/watchers`. Behaviour otherwise unchanged. Spec: "Home watches move to /watchers".

**Blocked by:** 02

Status: ready-for-agent

- [ ] `HaWatchesTests`, `HaWatchEffectsTests`, `WatchDelegationTests` and `HomeAssistantWatchFireTests` pass on `/watchers` paths.
- [ ] `/ha/watches/...` answers not-found and is absent from `/ha` listings and the setup index's tree.
- [ ] A watch written through `/watchers` carries a delegation and fires as before.
- [ ] Prompt snapshots and the skill snapshot updated; `WatchScenarios` armed run on the changed tier stays as green as on master.
