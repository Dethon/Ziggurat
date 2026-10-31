# 06: Agents built per household

**What to build:** The tracer bullet. Shipped agent templates (a barebones Jonas and Sugary) become documents; a turn's agent is built from its household's agents section (a template plus the household's name and custom instructions, or a seeded full definition), its household's settings are filled into the prompt (name, language, time zone), and its features decide which shared servers and domain features it gets. The agents in the agent's settings file are retired. Spec: `../spec.md` ("Agents per household").

**Blocked by:** 01, 03, 05

**Status:** done

- [x] A turn for a household runs that household's agent with its name, custom instructions, language and time zone in the prompt
- [x] A feature turned off removes its server and tools from the household's agents
- [x] A new household's agents are the two shipped templates
- [x] The settings file carries no agent definitions, and every eval, integration and E2E stack passes
- [x] A household-migration step seeds the home household's settings and its jack, jonas, nabu and sugary from today's definitions, re-runnable with a dry run

Notes:
- Contracts: `.claude/rules/household-config.md` ("The agents a household runs"), the template format
  in `.claude/rules/prompts.md` ("An agent template is a document"), the routing in
  `.claude/rules/channels.md`, the step in `.claude/rules/household-migration.md`.
- Decisions: the worker profiles (`subAgents`) stay the deployment's — a worker runs its parent's model
  for its parent's household, and the `workers` feature switches delegation; the per-channel defaults
  (`agentDefaults.byChannel`) stay the deployment's and the `fallback` is the household's
  `defaultAgent`; the household's *name* is not in the prompt (an agent's name is the entry's); the
  zone is named beside the day only where it is not the host's own, so the home's prompts are
  byte-identical to the snapshots; a turn acting for nobody runs the home's agents (`@home`).
- The seed route (12's) now lays a seeded document over what the household reads, so the settings
  keep the name the tenant row carries; `get/set-household-config --household home` names the home.
- Tests: seam 3 `MultiAgentFactoryHouseholdTests` (real factory, fake model at its address,
  `FakeOpenRouter`), `HouseholdAgentsTests`, `ChatMonitorAgentRoutingTests`; seam 6
  `HouseholdMigrationCommandTests` (seeded, kept, rerun, dry run), `HouseholdConfigSeedTests`;
  `TestAgentsTests` holds the copies equal to `HouseholdMigration/Home/agents.json`.
