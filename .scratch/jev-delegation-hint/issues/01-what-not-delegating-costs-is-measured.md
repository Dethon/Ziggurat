# 01 — What not delegating costs is measured

**What to build:** A measure, not a hint. Per conversation and per turn, the tokens the parent agent's context carries in tool results — the page reads, search results and long tool outputs a worker could have carried — and whether the turn delegated. Published through the existing metrics events (a field on what already exists where one fits; a new event only if none does), charted on the dashboard beside token usage, and readable from the eval's failure dump so a scenario's cost of doing research in place is a number. Follow `.claude/rules/observability.md`.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A turn that read two pages in place reports the tool-result tokens it added to the context; the same request delegated reports the worker's result tokens instead.
- [ ] A conversation's cumulative tool-result share of input tokens is chartable per agent on the dashboard.
- [ ] The eval dump of a run shows the figure per turn.
- [ ] A dated note under this spec's `## Comments` reads the figure off a week of production and says whether the hint is worth designing.
- [ ] Spec: `.scratch/jev-delegation-hint/spec.md` § What unblocks it.
