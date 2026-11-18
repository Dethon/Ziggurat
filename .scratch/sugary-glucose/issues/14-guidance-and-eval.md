# 14: Guidance, docs and eval for glucose and /watchers

**What to build:** `glucose-watches` skill (served by Sugary; shape, triggers, mg/dL conversion, effects, `person`, the crossing note, the outage-vs-silence distinction), `home-watches` refined for `/watchers`, the Sugary prompt/README cross-references, CONTEXT.md **Watch**/**Effect** generalised, rules (`sugary.md`, `virtual-filesystem.md`, `mcp-hosting.md`, `home-assistant.md`), eval: glucose watch scenarios (threshold, urgent night alarm, falling fast, sensor stopped, a grantee's watch) and a glucose analysis scenario over a seeded log; changed-tier run. Spec: "Guidance".

**Blocked by:** 07, 12, 13

Status: ready-for-agent

- [ ] Skill budgets and snapshots pass; the trigger claim loads the right skill for each kind.
- [ ] Changed-tier eval for watch and Sugary families is at least as green as master.
