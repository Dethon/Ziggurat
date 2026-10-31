# 12: The smoke tier checks the tunnel

**What to build:** A wiring mistake in the tunnel is caught before a deploy: the smoke script starts the home kit beside the smoke stack, and the smoke tests prove a signed-in reply that uses a tunnelled server, a browse page through the tunnel, and a forged tunnel credential refused. Spec: `../spec.md` (Testing Decisions, seam 5).

**Blocked by:** 10, 11

**Status:** done

- [x] A signed-in reply uses a tool of a tunnelled server
- [x] A browse page answers through the tunnel
- [x] A forged tunnel credential is refused
- [x] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`

Done: `scripts/smoke-stack.sh up` starts the kit beside the stack as project `tenant-kit-smoke`
(`TenantKit/docker-compose.override.smoke.yml`) — web search with Camoufox, vault, library, sandbox,
printer and the kit's Redis, never voice, Home Assistant's server, Home Assistant or Music Assistant —
with credentials minted from the stack's accounts service for its home household; the deployment's
in-network probe is a `smoke-probe` curl container in the deployment's smoke override. The first run
found a real defect: a server told the hub's CA built the chain from the leaf and the root alone, so
Caddy's intermediate-issued certificate was refused (fixed in `TunnelClient`, test authority reshaped
like Caddy's).
