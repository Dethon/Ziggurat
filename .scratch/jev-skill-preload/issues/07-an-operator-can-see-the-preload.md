# 07 — An operator can see the preload

**What to build:** One `SkillPreloadEvent` per judgment through `IMetricsPublisher` — agent, channel, outcome (`preloaded`, `abstained`, `none`, `deadline`, `error`, `skipped-lemonade`), the skills, the confidences, latency, input tokens — and a dashboard panel showing outcome share over time and judgment latency. A preload publishes no `ToolCallEvent`. Follow `.claude/rules/observability.md` for where an event type, its aggregation and its panel live.

**Blocked by:** 04

**Status:** resolved

- [x] Each outcome publishes exactly one event with the right outcome value; a skipped Lemonade turn publishes one too, with no latency.
- [x] Tool-call metrics for a turn with a preload and no model load count zero `load_skill` calls.
- [x] The dashboard shows the panel against the local stack, verified in a browser.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § Telemetry.

## Answer

Shipped 2026-09-18.

- `SkillPreloadEvent` (`Domain/DTOs/Metrics`, wire type `skill_preload`): outcome (`SkillPreloadOutcomes`:
  `preloaded`, `abstained`, `none`, `deadline`, `error`, `skipped-lemonade`), channel, skills, choice and its
  confidence, the per-skill needs, latency, input tokens, the Jev model; `AgentId`/`ConversationId` from the base.
  Published by `SkillPreloader` for every outcome it reaches (a skipped Lemonade turn with no latency; `NotAsked`
  publishes nothing) and by `ConversationGroup` for its own throw path. Agent, channel and conversation ride on
  `SkillPreloadRequest`; the provider path names the agent only.
- Collector: `metrics:skills:<day>` sorted set, `skills:<outcome>:count` and `skills:latency:{count,totalMs}` in the
  totals hash, `OnSkillPreload` to the hub. Query: grouped by `SkillPreloadDimension` (outcome, agent, channel,
  skill — by skill an event counts once per skill preloaded) × `SkillPreloadMetric` (count, latency with the
  aggregation pills, input tokens); `/skills`, `/skills/by/{dimension}`, `/skills/trend` (one series per outcome
  per hour/day bucket). Dashboard: `SkillsStore`/`SkillsState`, the eighth `MetricFamily`, `Skills.razor` (KPIs:
  judged, preloaded, deadline misses, errors, median latency; "Outcome share over time" on `LatencyTrendChart`;
  the breakdown; recent judgments), a drawn bolt in the sidebar.
- A preload publishes no `ToolCallEvent` (`McpAgentSkillPreloadTests.APreload_PublishesNoToolCallEvent`).
- Verified in a browser against the local stack (`observability` + `redis`, six events published on
  `metrics:events`): `http://localhost:5003/skills` — Judged 5 (the Lemonade skip is outside the denominator),
  Preloaded 2 (40%), Deadline 1 (20%), median 320 ms, all six outcomes on the breakdown and in the table. Note
  for next time: `up --build` reuses a stale `base-sdk`, build it first; and `dotnet user-secrets set` leaves the
  file 0600, which the container's `app` user cannot read — restore 0644.
