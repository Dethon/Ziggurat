# 07: Glucose on the /sugary mount

**What to build:** `glucose/<YYYY-MM>.parquet` and `sensors.parquet` on the mount, tables `glucose` and `sensors` in `query` (with `_local` siblings), `people/<folder>/` under the grant. README: columns, units (mg/dL, mmol/L conversion), the person's unit and target range, clinic statistics as copyable DuckDB SQL (time in ranges <54, <70, 70–180, >180, >250; mean; GMI; CV; coverage, and the 70 % coverage rule), `source` values incl. `import-hourly`; the line sending glucose to the HA mount replaced. Mount description lists the new files. Spec: "The mount".

**Blocked by:** 06

Status: done

- [x] Glob lists one glucose file per month with readings; a sandbox command reads it with duckdb through the bridge.
- [x] `query` joins `glucose` with `meals` on time windows; the README's statistic queries return the hand-computed figures on a seeded log.
- [x] A grantee reads another person's glucose under `people/<folder>/`; a non-grantee is refused.
- [x] README budget and snapshot updated.

## Comments

What was built:

- `Domain/DTOs/Sugary/Glucose.cs`: `GlucoseSource` and `GlucoseUnit` moved here from `McpServerSugary.Store` (the mount needs them; EF stores them as the same text, the model diff is empty), plus `LoggedGlucose(At, MgPerDl, Trend, Source)`, `LoggedSensor(Serial, ActivatedAt, LastSeenAt)`, `GlucoseTargets(Unit, Low, High)`.
- `ISugaryLog`: `GlucoseMonthsAsync(owner, zone)` (months on the household's clock with a reading; `SqliteSugaryLog` finds each month's first reading by the `(UserId, At)` index and jumps to the next month, `SugaryTables.NextMonthStart`, so a listing never loads every instant), `GlucoseAsync(owner, from, to)`, `GlucoseSensorsAsync(owner)`, `GlucoseTargetsAsync(owner)` (null without a link).
- `SugaryTables.Glucose` (`read_at`, `mg_dl`, `mmol_l` = mg/dL ÷ 18.016 rounded to one decimal away from zero, `trend` `falling_fast|falling|steady|rising|rising_fast|unknown`, `source` in the store's words) and `SugaryTables.Sensors` (`serial`, `activated_at`, `ends_at`, `last_seen_at`, each instant with `_local`). The source words now live in `SugaryTables.SourceWord/SourceOf`; `GlucoseReadingRules.Word/SourceOf` delegate to them, so the EF converter and any parallel branch calling them are unchanged.
- `SugaryPath`: `glucose/<YYYY-MM>.parquet` (`GlucoseMonth`), `glucose/` (`GlucoseDir`), `sensors.parquet` (`Sensors`). `SugaryFileSystem`: the files, the tables `glucose` and `sensors` in `query`, the same under `people/<folder>/`; `DescribeMount`, `DescribeGlob` and `DescribeQuery` name them.
- `Domain/Tools/Sugary/GlucoseQueries.cs`: `Statistics(since, until, low, high)` and `AfterMeals`, the statements the README prints and the tests run.
- `SugaryReadme.For(zone, targets)`: rendered per log (the README under `people/<folder>/` names that person's unit and range), a `glucose/<YYYY-MM>.parquet` section (columns, mg/dL, the mmol/L conversion, the four `source` values incl. `import-hourly`, their unit and range or "not linked" with the consensus range), `AfterMeals`, `sensors.parquet`, and "Glucose statistics". The lines that sent glucose to the Home Assistant mount (Asking, Conventions, periods) now point at `glucose`.
- Tests: `Tests/Unit/Domain/Sugary/SugaryGlucoseTablesTests.cs` (12: month files on Madrid's calendar, every statistic against hand figures, an empty period, the meal join, the README's statements and range, every column, the Parquet bytes, when the readings are loaded), `Tests/Integration/McpServerTests/SugaryGlucoseMountTests.cs` (3, over SQLite and MCP: months incl. a 23:30 UTC reading filed in the next month, the statistics over the store, the README's range), two people tests (a grantee reads another's glucose and README range; a non-grantee is refused query, bytes and README), and the sandbox E2E (`HerTables_AreReadByPolarsAndDuckDb`) reads `/sugary/glucose/*.parquet` with the duckdb CLI through the bridge. Listings in five existing tests gained `glucose/` and `sensors.parquet`.

The statistics, as ticket 08's `GlucoseStatistics` computes them (checked against it on branch `glucose-tab`):

- **Weights, not rows.** Over `[since, until)`, each reading stands for the minutes to the next reading in the period (the period's end for the last), at most 15 for every source, an `import-hourly` mean included (so an imported hour covers a quarter of itself: low resolution shows as low coverage); a longer gap is time not covered. Every figure uses that one weight, as the app's does. A literal "minutes with a reading" would give a fully sensored `graph`-only hour 20 % coverage, so this is the interpretation of the spec's coverage. The README says the figures are the ones the app shows.
- `below_54_pct`, `below_70_pct` (includes < 54), `in_70_180_pct` (70 ≤ v ≤ 180, the consensus range; the app has no such figure), `in_target_pct` (her low ≤ v ≤ high, the app's in range), `above_180_pct` (includes > 250), `above_250_pct`: share of covered minutes, %, one decimal.
- `mean_mg_dl`: time-weighted mean. `gmi_pct` = 3.31 + 0.02392 × mean (unrounded mean). `cv_pct` = 100 × sqrt(Σ w (v − mean)² / Σ w) / mean: the time-weighted **population** SD. `coverage_pct` = 100 × covered minutes / period minutes. All `round(x, 1)`.
- Hand-checked seed (Madrid, 00:00–02:00 on 1 Oct): ten minute currents (50, 60, 8 × 100), curve points 200, 260, 170 (gap of 40 → 15), an hourly 120 (60 to the end → 15); covered 50/120. Figures 2.0, 4.0, 76.0, 46.0 (range 80–160), 20.0, 10.0, mean 151.2, GMI 6.9, CV 33.8, coverage 41.7. A row count would give other figures.
- No readings in the period answers no row.

Decisions and deviations:

- **`query` loads `glucose` only for a statement that can reach it**: one naming `glucose`, or listing the database (`information_schema`, `duckdb_`, `show`, `pragma`), case-insensitive. Measured on a year of minute readings (525,600 rows) through SQLite and DuckDB: every statement cost ~650 ms before (`select count(*) from meals` included), 13 ms after for one about meals; a glucose statement is ~700 ms, a month file ~300 ms, glob 7 ms. A left-out table is absent, never empty, so a statement that reached it some other way fails rather than saying there were no readings.
- **`ends_at` is activation + 14 days for every sensor** (`SugaryTables.SensorLifetime`): LibreLinkUp's product type is kept but no lifetime per product is known here, and a guessed one would be a wrong date said as fact. The README says a longer-lived sensor ends later.
- **README/description budgets**: there is no budget or snapshot test for the README or the mount's description (they are mount text, not prompt sections); `SugaryWordsTests` (no she/her) covers both, now also a linked log's README. Sizes by `PromptTokens.Estimate`: README ~3,660 tokens (was ~2,290), mount description ~350.
- **Red first**: the tests were written after the code, so each was red only on compile; the guards were proven by mutation: the README read for the reader instead of the path's owner, and the months computed on UTC instead of the household's clock, each turned its test red. The statistics were checked against figures worked out before the first run.

For later tickets:

- **Ticket 14:** the `sugary-log` skill still sends glucose to `/ha` ("Every time is on the home's clock, the one the glucose history under `/ha` is stamped on", "glucose now (the sensor under `/ha`)", periods "read `/ha` history"), contradicting the README now; `McpSugaryServerTests.TheLogSkill_SaysWhereHerCycleIs_AndThatItsDaysLineUpWithHa` pins one of those lines, and the body has 7 tokens of headroom.
- **Ticket 08:** `GlucoseStatistics` must match the definitions above; `GlucoseQueries.Statistics` is the reference, and the hand-checked seed can be reused.
- **Torn reads:** the byte read re-renders a file on every ranged `ReadBlobAsync` (`FileSystemBackendBase`), so a sandbox read of the current month's file spanning several chunks while a poll lands can get bytes of two different files. The same was true of meals, but the glucose month changes every minute. Not fixed (it needs a snapshot per read).
