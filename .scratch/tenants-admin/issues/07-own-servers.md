# 07: Own servers

**What to build:** The admin area lists the household's own servers: add one by name and get its tunnel credential once (4a's credentials), see each server's connection state, connection time and tools from the relay, revoke a credential, which disconnects it. Spec: `../spec.md` ("Own servers").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A server added in the admin area connects with the credential shown once
- [ ] Its state, connection time and tools are shown while connected, and offline after it stops
- [ ] Revoking disconnects it at once and it cannot reconnect
