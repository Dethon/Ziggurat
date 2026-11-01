# 05: Agents

**What to build:** Per agent in the admin area: rename it, edit its custom instructions within a size limit, see the last few versions and restore one, add an agent from a shipped template and remove one. Nothing else about an agent is editable. Spec: `../spec.md` ("Agents").

**Blocked by:** 01

**Status:** done

- [x] A renamed agent with new instructions answers with them on the next turn
- [x] A restored version is what the agent uses afterwards
- [x] An agent added from a template appears in the household's chat client and Telegram `/agent` (both read the `agent-names` view, which lists it)
- [x] Instructions over the limit are refused

Decisions (`.claude/rules/admin.md`, "Agents"): versions are an accounts table (`AgentVersions`), the
state each change replaced, the last `Admin:AgentVersionsKept` per agent, dropped with the agent; the
default agent and the last agent are not removed (refused with the reason); a seeded definition's name
and instructions are edited in the definition itself, as the section's rules already place them; the
limits (`Admin:AgentInstructionsLength`, a name of 1–100) are the admin API's, not the operator
command's. Carry-in from 4b done: metrics key agents by id and carry `AgentName`, which the dashboard
shows; `StackSmokeTests` now matches the reply's usage on the agent's id and name (edited, not run here).
