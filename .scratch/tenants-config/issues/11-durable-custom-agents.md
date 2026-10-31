# 11: Durable custom agents

**What to build:** Custom agents registered through the agent API are kept in Redis per household and person, so they survive a restart and never resolve across households. Spec: `../spec.md` ("Agents per household").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** done

- [x] A custom agent survives an agent restart
- [x] A custom agent id never resolves for another household or person

Kept as one Redis hash per owner, `agents:custom:{householdId}:{userId}` (field: agent id, value: the definition as JSON). No household-migration step: custom agents were only ever in memory, so there is nothing stored to re-key.
