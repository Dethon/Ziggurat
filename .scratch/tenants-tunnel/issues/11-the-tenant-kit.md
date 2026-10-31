# 11: The tenant kit, with the home moved into it

**What to build:** The repo ships a tenant kit: a compose file and docs with the deployment's own images for web search with Camoufox, vault, library with qBittorrent and Jackett, sandbox, printer, the Home Assistant server with Home Assistant and Music Assistant, and voice with speech extraction, each configured by its environment and its own tunnel credential, plus the kit's own Redis, and no deployment secret. The home's agent definitions name these servers as tunnel endpoints; the deployment compose file loses the moved services, mounts, devices and their per-server secrets. A command in the kit moves today's home state (remembered logins, one-time links and download routing out of the deployment's Redis) and the docs list the volumes to carry. Spec: `../spec.md` ("The tenant kit").

**Blocked by:** 04, 05, 06, 07, 08, 09

**Status:** ready-for-agent

- [ ] The kit's compose holds no deployment secret and reaches the deployment only through the tunnel route (conformance)
- [ ] The deployment compose file no longer carries the moved services or their secrets (conformance)
- [ ] The home's agents name their servers as tunnel endpoints and every eval, integration and E2E stack passes
- [ ] The state-move command copies the home's state into the kit's Redis and says what it moved
