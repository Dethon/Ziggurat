# 16: The smoke tier checks household config

**What to build:** A wiring mistake is caught before a deploy: on a real stack after the migration, a signed-in reply comes from the home household's agent paid by its key, and the stores answer by household. Spec: `../spec.md` (Testing Decisions, seam 7).

**Blocked by:** 07, 15

**Status:** ready-for-agent

- [ ] A signed-in reply comes from the home household's agent with its configured name
- [ ] A topic and a schedule are listed under the home household
- [ ] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`
