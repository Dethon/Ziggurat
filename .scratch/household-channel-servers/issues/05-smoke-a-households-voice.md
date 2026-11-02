# 05: Smoke: a household's own voice answers it

**What to build:** `scripts/smoke-stack.sh voice` gains a household case beside the home's. A
non-home household is onboarded and adds the voice template's agent through the admin API. Its `voice`
credential is minted, and a second kit voice server and test satellite (a profile in
`TenantKit/docker-compose.override.smoke.yml`, like `mcp-vault-household`) start with it. The spoken
question is answered by that household's agent. A household without a voice agent hears the refusal.
The home's voice check is unchanged. Spec: `../spec.md`.

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] The household's satellite hears its own agent's answer to a question
- [ ] A timer set by voice in that household rings on its satellite
- [ ] A household with voice and no voice agent hears the refusal instead of a 120 s silence
- [ ] The home's voice check still passes beside it
- [ ] CLAUDE.md's smoke-tier description mentions the household voice case
