# 06: CORS allows only the deployment's origins

**What to build:** The agent and the SignalR channel replace `SetIsOriginAllowed(_ => true)` with `Cors:AllowedOrigins` from configuration, a per-deployment value in the compose environment (placeholder `changeme` where none is safe). Only listed origins get CORS headers, with credentials; empty means same-origin only. Spec: `../spec.md` ("CORS").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A preflight from a listed origin gets the CORS headers, from a foreign origin none, on both hosts (test host)
- [ ] WebChat through Caddy still connects to the hub (E2E suite passes)
