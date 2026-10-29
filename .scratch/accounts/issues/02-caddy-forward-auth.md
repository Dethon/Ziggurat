# 02: Caddy asks the accounts service about every web request

**What to build:** `forward_auth` to the accounts verify endpoint in front of WebChat, its hub, attachments and dictation, `/api/agents`, `sexy.*`, `sugary.*` and the dashboard, copying `X-Ziggurat-User`, `X-Ziggurat-Tenant` and `X-Ziggurat-Roles` onto the upstream request so a client-sent one can't survive. The dashboard admits the operator only. `/browse/*`, the logins page and the accounts service's own pages stay outside. Spec: `../spec.md` ("forward_auth", "The dashboard").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A Caddyfile conformance test asserts every protected route has `forward_auth` with the header copy and `/browse/*` has none
- [ ] A signed-out browser hitting WebChat lands on sign-in; a signed-in one reaches WebChat (E2E)
- [ ] A member is refused the dashboard; the operator reaches it
