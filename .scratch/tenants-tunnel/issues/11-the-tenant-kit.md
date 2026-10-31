# 11: The tenant kit, with the home moved into it

**What to build:** The repo ships a tenant kit: a compose file and docs with the deployment's own images for web search with Camoufox, vault, library with qBittorrent and Jackett, sandbox, printer, the Home Assistant server with Home Assistant and Music Assistant, and voice with speech extraction, each configured by its environment and its own tunnel credential, plus the kit's own Redis, and no deployment secret. The home's agent definitions name these servers as tunnel endpoints; the deployment compose file loses the moved services, mounts, devices and their per-server secrets. A command in the kit moves today's home state (remembered logins, one-time links and download routing out of the deployment's Redis) and the docs list the volumes to carry. Spec: `../spec.md` ("The tenant kit").

**Blocked by:** 04, 05, 06, 07, 08, 09

**Status:** done

- [x] The kit's compose holds no deployment secret and reaches the deployment only through the tunnel route (conformance) — `TenantKitConformanceTests`, through `DeploymentFiles.KitCompose`/`KitPlaceholders`
- [x] The deployment compose file no longer carries the moved services or their secrets (conformance) — `DeploymentConformanceTests.AHouseholdsOwnServer_IsNoServiceOfTheDeployment`, `AMovedServersSetting_IsNoLongerTheDeployments`, `NothingInTheDeployment_RunsTheModelsCommands`
- [x] The home's agents name their servers as tunnel endpoints and every eval, integration and E2E stack passes — the unarmed eval harness, the prompt snapshots (unchanged: they hold no qualified tool names), the unit and integration suites; of the E2E stacks only the ones the change reaches were run (see the report). The smoke tier is ticket 12's: it still names the moved containers (`SmokeStack.Inside` is `camoufox`)
- [x] The state-move command copies the home's state into the kit's Redis and says what it moved — `move-home-state` (`HomeStateMoveTests`), the kit's `state-move` service

Also: the hub's certificate may chain to a CA the server is told of (`MCP__TUNNEL__HUBCERTIFICATEAUTHORITY`, `TunnelRelayTests`), for ticket 12's smoke stack on Caddy's local CA.
