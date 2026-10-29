# 12: SexyTime behind the same sign-in

**What to build:** `/api/agents` takes the user from the forwarded headers and drops the `userId` query parameter; the agent-API secret stays for SexyTime's server calls. `sexy.*` is protected by ticket 02, and its hub connection is the signed-in user. Spec: `../spec.md` ("SexyTime").

**Blocked by:** 02, 03

**Status:** ready-for-agent

- [ ] A custom agent registered through `/api/agents` belongs to the forwarded user; a `userId` parameter is ignored (agent API tests)
- [ ] A person sees only their own custom agents
