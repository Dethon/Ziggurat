# 02: Outposts register through Caddy

**What to build:** An outpost registers with the hub at `https://assistants.<domain>` instead of the agent's plain port. `assistants.*` gets a `handle /api/outposts*` route to the agent; the bridge stays unrouted. The outpost rules and the `--hub` example in README are updated; the agent's dial-back to the machine is unchanged. Spec: `../spec.md` ("Outposts").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The Caddyfile test asserts `/api/outposts` reaches the agent and `/api/vfs-bridge` still doesn't
- [ ] `.claude/rules/outpost.md` and README show the hub as the Caddy URL
- [ ] An outpost registering through the route is accepted with its secret and refused without (prior art: the outpost API tests)
