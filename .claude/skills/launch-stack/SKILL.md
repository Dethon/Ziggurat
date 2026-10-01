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
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips `/dev/dri` from `plex`/`mcp-sandbox`/`lemonade` (and forces `lemonade` STT to CPU) on hosts without a DRI render node |

## Launching

Swap the override for your OS (`linux` on Linux/WSL, `windows` on Windows):

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build \
  agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library \
  mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer mcp-timers \
  lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox`/`lemonade` for GPU acceleration. Only the render node is mapped — the Vulkan tier needs nothing more, so `/dev/dri` without `/dev/kfd` (Intel iGPU, Raspberry Pi) still comes up; `/dev/kfd` (ROCm) is never mapped. The opt-in NPU tier (`docker-compose.override.npu.yml`) maps `/dev/accel/accel0` instead. Hosts with **no** DRI render node (NVIDIA-only WSL2 has `/dev/dxg`, never `/dev/dri`) fail with `error gathering device information while adding custom device "/dev/dri"` — append `-f DockerCompose/docker-compose.override.no-dri.yml` last to strip it (the VS Code `docker-debug-up` task already does).

## Secrets

Services read secrets from .NET User Secrets mounted at `/home/app/.microsoft/usersecrets`; the OS override files map the host-side path. A crash with `Value cannot be an empty string. (Parameter 'connectionString')` means they aren't mounted — check you're using the right override.

Two secrets live in `DockerCompose/.env` and must be set (any non-empty value) before the stack is useful:

- `MCP__SHAREDSECRET` — every MCP server's `/mcp` (channel servers included) refuses a call without it, and the agent presents it on every configured endpoint and channel connection. One value reaches both ends. Unset, the agent's session builds and channel connections fail with 401 — by design, not a startup bug. Anything you point at a server's `/mcp` by hand (an MCP inspector, a script) sends `Authorization: Bearer <value>`.
- `AGENTAPI__SHAREDSECRET` — `/api/agents` (custom-agent registration, routed by Caddy) refuses every verb without `Authorization: Bearer <value>`.

## Accessing the WebChat & Dashboard

Caddy (port 443, Let's Encrypt TLS) is the entry point: `/hubs/*` → McpChannelSignalR, `/dashboard/*` → Observability, everything else → WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

The dashboard (an installable PWA) is at `https://assistants.herfluffness.com/dashboard/` or `http://localhost:5003/dashboard/` direct.

## Debugging with Playwright

Use `ignoreHTTPSErrors: true` for the browser context locally (the certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, or sends are silently rejected with a toast error.
