# 01: Only Caddy faces the network

**What to build:** The base compose file publishes Caddy's 443, Redis on loopback, and the operator's LAN tools (qBittorrent UI and peer port, filebrowser, Jackett, Home Assistant, Lemonade) on `${LAN_BIND_ADDRESS}`, with `127.0.0.1` as the fallback and a placeholder in `.env`. Every other port leaves the base file for a new opt-in `docker-compose.override.ports.yml`, which publishes them on `127.0.0.1` at today's port numbers so host-run debugging (`appsettings.Local.json`, `.vscode/launch.json`, `.zed/debug.json`) keeps working. Spec: `../spec.md` ("Published ports", "Debug override").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A compose conformance test fails while any base-file port other than Caddy's 443 is published without a bind address, and passes after
- [ ] Redis is published on `127.0.0.1:6379` only
- [ ] The LAN services bind to `${LAN_BIND_ADDRESS}`, documented in `.env` with what to set it to
- [ ] The debug override publishes only on `127.0.0.1`, asserted by the same test
- [ ] The VS Code debug tasks and the `launch-stack` skill bring the stack up with the override
