# 08: The Glucose tab in the app

**What to build:** Server: `GlucoseStatistics` (one implementation: ranges, mean, GMI, CV, coverage) and routes for current reading + link state, readings over a window, and statistics over 1/7/14/90 days. Client: a Glucose tab with current reading, drawn trend arrow and age (stale past 15 min), a curve with the target band for 24 h / 7 d / 14 d / 90 d, the statistics, the link form and settings (unit, range), every link failure worded in both languages with what to do. Spec: "The app".

**Blocked by:** 06

Status: ready-for-agent

- [ ] `GlucoseStatistics` unit tests against hand-computed figures, including gaps and low coverage.
- [ ] vitest for unit conversion, trend mapping, staleness and the error wording.
- [ ] Icons are inline SVG; strings in both languages; screenshot check with the mock harness.
