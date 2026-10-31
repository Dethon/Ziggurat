# 06: Agents built per household

**What to build:** The tracer bullet. Shipped agent templates (a barebones Jonas and Sugary) become documents; a turn's agent is built from its household's agents section (a template plus the household's name and custom instructions, or a seeded full definition), its household's settings are filled into the prompt (name, language, time zone), and its features decide which shared servers and domain features it gets. The agents in the agent's settings file are retired. Spec: `../spec.md` ("Agents per household").

**Blocked by:** 01, 03, 05

**Status:** ready-for-agent

- [ ] A turn for a household runs that household's agent with its name, custom instructions, language and time zone in the prompt
- [ ] A feature turned off removes its server and tools from the household's agents
- [ ] A new household's agents are the two shipped templates
- [ ] The settings file carries no agent definitions, and every eval, integration and E2E stack passes
