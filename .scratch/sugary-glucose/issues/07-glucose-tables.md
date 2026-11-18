# 07: Glucose on the /sugary mount

**What to build:** `glucose/<YYYY-MM>.parquet` and `sensors.parquet` on the mount, tables `glucose` and `sensors` in `query` (with `_local` siblings), `people/<folder>/` under the grant. README: columns, units (mg/dL, mmol/L conversion), the person's unit and target range, clinic statistics as copyable DuckDB SQL (time in ranges <54, <70, 70–180, >180, >250; mean; GMI; CV; coverage, and the 70 % coverage rule), `source` values incl. `import-hourly`; the line sending glucose to the HA mount replaced. Mount description lists the new files. Spec: "The mount".

**Blocked by:** 06

Status: ready-for-agent

- [ ] Glob lists one glucose file per month with readings; a sandbox command reads it with duckdb through the bridge.
- [ ] `query` joins `glucose` with `meals` on time windows; the README's statistic queries return the hand-computed figures on a seeded log.
- [ ] A grantee reads another person's glucose under `people/<folder>/`; a non-grantee is refused.
- [ ] README budget and snapshot updated.
