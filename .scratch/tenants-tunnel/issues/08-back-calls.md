# 08: Back-calls from a household's server

**What to build:** A tunnelled server reaches back to the deployment only over its tunnel and only to an allowlist the relay enforces: the accounts service's resolution and guest endpoints (voice), the agent's VFS bridge (sandbox), the metrics feed, the shared Lemonade, and the topic-minting API. Each back-call carries the tunnel's identity; anything else is refused. Spec: `../spec.md` ("Back-calls").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] Each allowlisted back-call works through the tunnel and says which household's server asked
- [ ] A back-call to anything else is refused and reported
- [ ] A sandbox command reads and writes another mount through the bridge over the tunnel
