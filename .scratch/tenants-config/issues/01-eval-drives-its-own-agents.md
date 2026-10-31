# 01: The eval drives its own agent copies

**What to build:** The eval reads the agents it drives from its own copies of today's definitions under the tests, not from the agent's settings file, so retiring those definitions later changes nothing it measures. Prefactor, no behaviour change. Spec: `../spec.md` ("Prompts, skills and templates as documents").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] The eval stack builds every agent from the copies under the tests
- [ ] The unarmed eval harness tests and the prompt snapshots pass unchanged
