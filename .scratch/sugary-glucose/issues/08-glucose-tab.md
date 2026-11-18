# 08: The Glucose tab in the app

**What to build:** Server: `GlucoseStatistics` (one implementation: ranges, mean, GMI, CV, coverage) and routes for current reading + link state, readings over a window, and statistics over 1/7/14/90 days. Client: a Glucose tab with current reading, drawn trend arrow and age (stale past 15 min), a curve with the target band for 24 h / 7 d / 14 d / 90 d, the statistics, the link form and settings (unit, range), every link failure worded in both languages with what to do. Spec: "The app".

**Blocked by:** 06

Status: done

- [x] `GlucoseStatistics` unit tests against hand-computed figures, including gaps and low coverage.
- [x] vitest for unit conversion, trend mapping, staleness and the error wording.
- [x] Icons are inline SVG; strings in both languages; screenshot check with the mock harness.

## Comments

What was built:

- `McpServerSugary/Glucose/GlucoseStatistics.cs`: `GlucoseStatistics.Of(readings, from, to, targetLow, targetHigh) -> GlucoseFigures(Count, Coverage, Mean, Gmi, Cv, VeryLow, Low, InRange, High, VeryHigh)`. Each reading in `[from, to)` weighs the minutes until the next, at most 15 (`Cap`), the last until `to`. Coverage is the weighed minutes over the period's. Shares are of the weighed (covered) minutes. Mean and SD are weighed the same way (population SD), CV = SD / mean, GMI = 3.31 + 0.02392 x mean. Everything but `Count` and `Coverage` is null with no readings, because NaN would be a 500.
- `McpServerSugary/Glucose/GlucoseCurve.cs`: `BucketFor(window)` (as read up to 2 days, 15-minute means up to 16 days, hourly beyond) and `Of(readings, bucket)`. Buckets are aligned on UTC, each point is at its bucket's middle and rounded to 0.1, and an empty bucket has no point.
- `McpServerSugary/App/GlucoseEndpoints.cs`, all GETs (not queued by `OneLogWriteAtATime`) and bound to the person:
  - `GET /current` returns `{reading: {at, mgPerDl, trend, ageSeconds} | null, link: <the link answer> | null, canLink, unit, targetLow, targetHigh}`. It does not 404 without a link, because readings outlive it. Without a link it answers mg/dL, 70/180.
  - `GET /readings?from&to` returns `{from, to, bucketMinutes | null, points: [{at, mgPerDl}]}`. Without a window it covers the last day. `400 bad-window` when from >= to; `400 window-too-long` past 90 days.
  - `GET /statistics?days=1|7|14|90` (default 14) returns `{days, from, to, targetLow, targetHigh, count, coverage, mean, gmi, cv, veryLow, low, inRange, high, veryHigh}`. Any other value of `days` is `400 bad-period`. The period ends at the server's now.
- Client (`Sugary.Client/src`):
  - `glucose.ts` (pure): unit conversion (mmol/L = mg/dL / 18.016, 1 decimal in her locale; mg/dL whole), `trendOf` (one drawn arrow rotated -90/-45/0/45/90; unknown, or anything not recognised, is the drawn `trendUnknown`), `ageMinutes`/`isStale` (> 15 min)/`ageText`, `band`, `windowOf`, `curveSegments` (SVG path per stretch, broken past max(15 min, 2 buckets), values clamped to the frame), `formatShare`, `rangeProblem`, `linkFailureText` (the poller's stored failure) and `glucoseErrorMessage` (a refusal of what she just did, by `code`).
  - `screens/GlucoseScreen.tsx`: current reading (stale notice; "no readings since X" notice with what to do), period picker, curve card, statistics card (5 band rows with bars, mean, GMI, CV, coverage and count, a note under 70 % coverage), and the sensor card: link form (email, password, region select with Automatic), connection choice, linked name with state, "Link again" (the form opens on its own after `credentials-refused`), unit and range form, unlink with confirmation.
  - `components/GlucoseChart.tsx`: inline SVG drawn by hand (no chart dependency): target band with dashed edges, edge and top labels in her unit, three time ticks, and a pointer or touch readout of the nearest point.
  - Also: `ApiError.code` now carries a problem's `code` (`api.ts`); icons `curve` (the tab), `trend` and `trendUnknown` in `Icon.tsx`; tokens `--color-glucose-band(-edge)` in both themes; strings in en and es.
  - The tab sits between Log and Cycle. Six tabs at 360 px: `.tab` is `nowrap` and never narrower than its label, so "Despensa" takes 64 px and the others 59 px. That was measured, not assumed.
- Tests: `Tests/Unit/McpServerSugary/GlucoseStatisticsTests.cs` (11, hand figures: even, cumulative bands, boundaries, uneven spacing where weighed and plain differ (162.5 vs 133.3), a gap, the last reading clipped at the period's end, one reading in a day (coverage 15/1440), none, outside the period, out of order, her own range), `GlucoseCurveTests.cs` (9), `Tests/Integration/McpServerTests/SugaryGlucoseApiTests.cs` (16: no readings, newest with arrow and age, link and her unit/range, only hers, each bucket size, window bounds, default window, a day's figures, her range, no readings, bad periods). `src/glucose.test.ts` (55, including that the problem's code reaches `ApiError` through `request`, every refusal code worded in Spanish, and that no glucose string carries a typed arrow or pictograph). `npm test` 1978 passed, `npm run build` green. `Tests/Unit` and the Sugary integration tests: 7910 passed, 0 failed.

Decisions and deviations:

- **The bands are cumulative and fixed; in range is her range.** <54, <70, >180, >250 are the consensus thresholds whatever her range. With a range other than 70-180 the five shares do not sum to 100, so the app shows each as its own bar, never a stacked one, and says that low includes very low.
- **Mean and SD are time-weighted too**, not only the shares. Kept readings are uneven (current about every 3 minutes, graph every 5, import hourly), so a plain mean would weigh a stretch by how often it was read.
- **Quarter-hour buckets for 7/14 days, not 5-minute ones.** Five-minute means for 14 days would be about 4,000 points (180 KB of JSON) on a chart about 300 px wide. Fifteen minutes gives about 1,300 points, and hourly gives about 2,200 for 90 days.
- **Settings live in the Glucose tab's sensor card**, not in Settings: they exist only with a link. The range is typed in her unit and kept in mg/dL. An untouched field goes back as recorded (`overRecord`/`useShown`), so 3.9 mmol/L (70.26) is never sent as her change and a stale save gets no false 409.
- **Two wordings for rate-limited and unavailable**: on the stored link state Sugary retries by itself ("nothing to do"); on a link she just tried, "try again in a few minutes". `no-connections` and `linking-off` come only from `PUT /link` (and `canLink`).
- **Screenshots:** the mock harness from memory (`.scratch/sugary/design/brief.md` §0) is gone (no brief, no `capture.mjs`). An ad-hoc equivalent was run instead: Playwright from `Tests/bin`, `vite` dev server, every `/api` call mocked, 360x760 at 2x. It covered en-light and es-dark, linked, mmol with a step-required failure and a stale reading, not linked, choose a connection, linking off, and the 14 d hover. It caught the Spanish tab bar overlap ("Despensa" ran into "Ajustes"), the unstyled connection buttons, and a band border on the empty card; all three are fixed. The script and shots are in the session scratchpad, which is not durable.

For ticket 07 (README SQL): the app's figures weigh each reading by `least(next.read_at - read_at, 15 min)`, the last until the period's end, and mean/SD are weighed the same way. Plain `avg(mg_dl)` will differ a little where spacing is uneven. Either give the weighted SQL (a `lead()` window) or say that the app's are time-weighted.

For ticket 09 (import): `import-hourly` readings each weigh at most 15 minutes, so an hourly-only stretch reads as 25 % coverage. That is by the rule as specified; the app will flag such a period as low coverage.
