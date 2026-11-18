# 10: Glucose watches as files at /watchers

**What to build:** `GlucoseWatch` storage (household id, subject, writer, spec JSON, enabled, once, delegation slot, createdAt) and migration; `SugaryWatchFileSystem` (`sugary-watches`, mount `/watchers`, union claims `glucose`, `glucose_trend`, `glucose_forecast`, `sensor`) registered beside `sugary`. `GlucoseWatchSpec` parses and refuses by field name (spec's fields, `time` conditions only, no `actions`); `person` resolves a `people/` folder under the grant; `deliverTo`/`userId` filled from the caller like a home watch; listed for subject, writer and grantees; nobody refused. `status.json`: createdAt, lastFiredAt, current condition per trigger, spent. Announce targets validated at write via `ISatelliteCatalog` (ticket 13 wires the relay; until then a target is refused as unavailable). Spec: "Glucose watches — Files", "Who".

**Blocked by:** 02, 06

Status: ready-for-agent

- [ ] Write/read/edit/remove/glob through the union beside a fallback member; ids unique per household.
- [ ] Every refused field names itself; another person's sensor without the grant is `permission_denied`.
- [ ] A non-grantee in the household does not list another's glucose watch.
