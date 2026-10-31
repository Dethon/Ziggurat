# 09: Shared servers reach a household's server

**What to build:** A shared deployment server reaches a household's own server through the relay's internal endpoint, never the network: timers announce, dismiss and resolve satellites on the household's voice server. Spec: `../spec.md` ("The tunnel").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A timer for the home household rings, is dismissed and validates its satellite through the relay
- [ ] A shared server cannot reach a household's server outside the relay's endpoint
- [ ] An offline voice server makes a timer's create fail clearly
