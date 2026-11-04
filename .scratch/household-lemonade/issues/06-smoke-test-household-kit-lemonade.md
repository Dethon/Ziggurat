# 06: Smoke test for a household's kit Lemonade box

**What to build:** On the live smoke stack, a household goes through the whole setup for a Lemonade box behind its own router:
- it saves a kit box on its Keys page through the admin API;
- it mints its `lemonade` credential;
- it starts a forwarder in the smoke kit, pointed at the key stub's Lemonade model list.

Then its agent's picker offers that box's model, and the home's picker doesn't. A smoke-stack verb of its own starts the forwarder, as the household vault one does.

**Blocked by:** 03, 04.

**Status:** ready-for-agent

- [ ] A new smoke-stack verb starts a household forwarder in the smoke kit on a credential passed to it, pointed at the key stub's Lemonade answer.
- [ ] A smoke test (skipped unless the stack is up, like the others):
  - creates a household and saves its kit box;
  - mints `lemonade` and starts the forwarder through the verb;
  - sees the Keys page status turn "connected";
  - sees the stub's model in that household's picker within a refresh, and never in the home's.
- [ ] The key stub answers the forwarder's model listing; if it already does, nothing changes there.
- [ ] Every connection stays on 127.0.0.1. Nothing reaches the deployment's host.
- [ ] The project's smoke-tier description lists the new test and verb.
