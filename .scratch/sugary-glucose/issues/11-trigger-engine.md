# 11: Evaluating glucose triggers

**What to build:** `GlucoseTriggers` — a pure evaluation of a watch's triggers and conditions over recent readings, link state, sensor facts, persisted `GlucoseWatchState` and the clock, answering new state and which triggers fired. Threshold, trend, forecast and sensor states as the spec defines; `for`; crossing-only; `once`; time conditions on the household's clock. Called after each poll for that person; only on a newest reading under 10 min old; state persisted. A watch written while its condition holds starts as holding-and-fired. Spec: "Triggers", "Evaluation".

**Blocked by:** 06, 10

Status: ready-for-agent

- [ ] Table-driven tests per trigger kind: crossing, holding without re-firing, leaving and re-crossing, `for` interrupted.
- [ ] `no_readings` fires on a silent sensor and never on a link failure; `link_failing` the reverse.
- [ ] Back-filled and imported readings change nothing; a restart (state reloaded) neither re-fires nor loses a pending `for`.
