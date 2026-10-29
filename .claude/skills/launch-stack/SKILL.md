---
name: launch-stack
description: Launch the local jackbot Docker Compose stack and reach its UIs — compose files and OS overrides, the full service list, secrets mounts, WebChat/Dashboard access through Caddy, and Playwright debugging notes.
---

# Launching the local stack

## Docker Compose files

| File                                                | Purpose |
|-----------------------------------------------------|---------|
| `DockerCompose/docker-compose.yml`                  | Main service definitions |
| `DockerCompose/docker-compose.override.windows.yml` | Windows user secrets mount (`%APPDATA%/Microsoft/UserSecrets`) |
| `DockerCompose/docker-compose.override.linux.yml`   | Linux user secrets mount (`$HOME/.microsoft/usersecrets`) |
| `DockerCompose/docker-compose.override.ports.yml`   | Publishes the MCP servers, channel servers, agent, webui, observability, Camoufox and tse-extractor on `127.0.0.1` at their usual ports, for a project run on the host against the stack (`appsettings.Local.json`). The base file publishes only Caddy's 443, Redis on loopback and the operator's LAN tools on `${LAN_BIND_ADDRESS}` (loopback when unset) |
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips `/dev/dri` from `plex`/`mcp-sandbox`/`lemonade` (and forces `lemonade` STT to CPU) on hosts without a DRI render node |

## Launching

To read prod's Redis, tunnel to its loopback port: `ssh -L 6379:127.0.0.1:6379 192.168.5.45`, then connect to `localhost:6379`.

Swap the override for your OS (`linux` on Linux/WSL, `windows` on Windows):

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build \
  agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library \
  mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer mcp-timers mcp-sugary \
  lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox`/`lemonade` for GPU acceleration. Only the render node is mapped — the Vulkan tier needs nothing more, so `/dev/dri` without `/dev/kfd` (Intel iGPU, Raspberry Pi) still comes up; `/dev/kfd` (ROCm) is never mapped. The opt-in NPU tier (`docker-compose.override.npu.yml`) maps `/dev/accel/accel0` instead. Hosts with **no** DRI render node (NVIDIA-only WSL2 has `/dev/dxg`, never `/dev/dri`) fail with `error gathering device information while adding custom device "/dev/dri"` — append `-f DockerCompose/docker-compose.override.no-dri.yml` last to strip it (the VS Code `docker-debug-up` task already does).

## Secrets

Services read secrets from .NET User Secrets mounted at `/home/app/.microsoft/usersecrets`; the OS override files map the host-side path. A crash with `Value cannot be an empty string. (Parameter 'connectionString')` means they aren't mounted — check you're using the right override.

These secrets live in `DockerCompose/.env` and must be set (any non-empty value) before the stack is useful:

- `MCP_SECRET_<SERVER>` (one per MCP server, channel servers included) — each server's `/mcp` refuses a call without its own, which it reads as `MCP__SHAREDSECRET`; the agent holds them all as `MCP__SECRETS__<host>` and refuses to start without one for a server it dials. Anything you point at a server's `/mcp` by hand (an MCP inspector, a script) sends `Authorization: Bearer <that server's value>`.
- `REDIS_PASSWORD` — compose refuses to start the stack without it.
- `AGENTAPI__SHAREDSECRET` — `/api/agents` (custom-agent registration, routed by Caddy) refuses every verb without `Authorization: Bearer <value>`.

## Accessing the WebChat & Dashboard

Caddy (port 443, Let's Encrypt TLS) is the entry point: `/hubs/*` → McpChannelSignalR, `/dashboard/*` → Observability, everything else → WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

The dashboard (an installable PWA) is at `https://assistants.herfluffness.com/dashboard/` or `http://localhost:5003/dashboard/` direct.

## The Sugary app

`mcp-sugary` serves her carb-counting PWA (`Sugary.Client`, built into the image) on every route but `/mcp`: `http://localhost:6017/`. The compose file switches its access code off (`SUGARYAPP__REQUIREACCESSCODE: "false"`) because the deployment is reached only through the VPN; with it on, it asks for `SUGARYAPP__ACCESSCODE` from `DockerCompose/.env` once per browser and keeps a cookie, and an unset code refuses every sign-in. Caddy fronts it at `https://sugary.herfluffness.com` (the site refuses `/mcp`, allows 230 MiB bodies for a filmed meal, and sets the `X-Forwarded-For` the sign-in limit counts by), which installing it to a phone and live barcode scanning need. Port 6017 is published on loopback only, for local runs and the E2E. For frontend work, `npm run dev` in `Sugary.Client` proxies `/api` to that port; `npm run build` writes into `McpServerSugary/wwwroot`, which `dotnet run` serves when started from the project directory.

## Debugging with Playwright

Use `ignoreHTTPSErrors: true` for the browser context locally (the certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, or sends are silently rejected with a toast error.
