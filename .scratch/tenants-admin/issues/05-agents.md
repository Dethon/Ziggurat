# 05: Agents

**What to build:** Per agent in the admin area: rename it, edit its custom instructions within a size limit, see the last few versions and restore one, add an agent from a shipped template and remove one. Nothing else about an agent is editable. Spec: `../spec.md` ("Agents").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A renamed agent with new instructions answers with them on the next turn
- [ ] A restored version is what the agent uses afterwards
- [ ] An agent added from a template appears in the household's chat client and Telegram `/agent`
- [ ] Instructions over the limit are refused
