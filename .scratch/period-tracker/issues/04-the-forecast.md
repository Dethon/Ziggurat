# 04 — The forecast: the next period and the fertile window

**What to build:** Once she has two periods, a forecast card shows the expected next period as a
range with its likeliest day, the fertile window while it is not past, how many cycles it is based
on and the sentence that it is an estimate, not reliable for avoiding pregnancy. The calendar draws
the expected and fertile days with a dashed outline and a tint, and the legend says so.

**Blocked by:** 03

**Status:** ready-for-agent

Spec: `.scratch/period-tracker/spec.md`

- [x] Under vitest: no forecast before two periods; up to six recent cycles; a cycle over 1.5 × the median left out; range and likeliest day; fertile window from 19 days before the earliest to 13 before the latest; an ongoing period forecast from its start.
- [x] The estimate sentence is shown with every forecast, in both languages.
- [x] Forecast days are drawn differently from recorded ones, in both colour schemes.
