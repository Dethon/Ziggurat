# 11: Durable custom agents

**What to build:** Custom agents registered through the agent API are kept in Redis per household and person, so they survive a restart and never resolve across households. Spec: `../spec.md` ("Agents per household").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] A custom agent survives an agent restart
- [ ] A custom agent id never resolves for another household or person
