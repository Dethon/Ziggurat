# 07 — An operator can see the preload

**What to build:** One `SkillPreloadEvent` per judgment through `IMetricsPublisher` — agent, channel, outcome (`preloaded`, `abstained`, `none`, `deadline`, `error`, `skipped-lemonade`), the skills, the confidences, latency, input tokens — and a dashboard panel showing outcome share over time and judgment latency. A preload publishes no `ToolCallEvent`. Follow `.claude/rules/observability.md` for where an event type, its aggregation and its panel live.

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] Each outcome publishes exactly one event with the right outcome value; a skipped Lemonade turn publishes one too, with no latency.
- [ ] Tool-call metrics for a turn with a preload and no model load count zero `load_skill` calls.
- [ ] The dashboard shows the panel against the local stack, verified in a browser.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § Telemetry.
