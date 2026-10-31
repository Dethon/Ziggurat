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
| `DockerCompose/docker-compose.override.ports.yml`   | Publishes the MCP servers, channel servers, agent, webui, observability, accounts, Camoufox and tse-extractor on `127.0.0.1` at their usual ports, for a project run on the host against the stack (`appsettings.Local.json`). The base file publishes only Caddy's 443, Redis on loopback and the operator's LAN tools on `${LAN_BIND_ADDRESS}` (loopback when unset) |
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips `/dev/dri` from `plex`/`mcp-sandbox`/`lemonade` (and forces `lemonade` STT to CPU) on hosts without a DRI render node |

## Launching

Every server needs its own `MCP_SECRET_<SERVER>` and the stack needs `REDIS_PASSWORD` (compose refuses to start without it). For a local stack, export each `MCP_SECRET_*` as `local-mcp-secret`: a project run on the host as the agent dials every server at `localhost` and holds that one value as its `localhost` credential (`.vscode/launch.json`, `.zed/debug.json`, `scripts/run-local.sh`). The Local profiles connect to Redis with `local-redis-password`, which the editor tasks start it with. Holding a server's credential lets a caller talk to it, not act through it: every tool call must also carry the agent's call token for that server. Compose tells each server its own name (`MCP__AUDIENCE`) and where the agent publishes its keys (`MCP__AGENTKEYSURL`, `http://agent:8080/internal/jwks`, never routed by Caddy); the agent generates its key into `./volumes/agent-keys` on first start (`agent-volume` hands the folder to PUID). A host run of the agent keeps its key in `Agent/.signing/` (`appsettings.Local.json`), and the editor profiles and `scripts/run-local.sh` tell every host-run server `localhost` and `http://localhost:5000/internal/jwks` — a server refuses tool calls until the agent is up to publish them. Something you point at a server's `/mcp` by hand gets through the handshake and the tool list, and is refused on every tool call (`Refused (Missing)`).

To read prod's Redis, tunnel to its loopback port: `ssh -L 6379:127.0.0.1:6379 192.168.5.45`, then connect to `localhost:6379`.

Swap the override for your OS (`linux` on Linux/WSL, `windows` on Windows):

```bash
REDIS_PASSWORD=local-redis-password docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -f DockerCompose/docker-compose.override.ports.yml -p jackbot up -d --build \
  agent webui observability accounts mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library \
  mcp-channel-signalr mcp-channel-telegram mcp-channel-voice mcp-scheduling mcp-printer mcp-timers mcp-sugary \
  lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox`/`lemonade` for GPU acceleration. Only the render node is mapped — the Vulkan tier needs nothing more, so `/dev/dri` without `/dev/kfd` (Intel iGPU, Raspberry Pi) still comes up; `/dev/kfd` (ROCm) is never mapped. The opt-in NPU tier (`docker-compose.override.npu.yml`) maps `/dev/accel/accel0` instead. Hosts with **no** DRI render node (NVIDIA-only WSL2 has `/dev/dxg`, never `/dev/dri`) fail with `error gathering device information while adding custom device "/dev/dri"` — append `-f DockerCompose/docker-compose.override.no-dri.yml` last to strip it (the VS Code `docker-debug-up` task already does).

## Secrets

Services read secrets from .NET User Secrets mounted at `/home/app/.microsoft/usersecrets`; the OS override files map the host-side path. A crash with `Value cannot be an empty string. (Parameter 'connectionString')` means they aren't mounted — check you're using the right override.

These secrets live in `DockerCompose/.env` and must be set (any non-empty value) before the stack is useful:

- `MCP_SECRET_<SERVER>` (one per MCP server, channel servers included) — each server's `/mcp` refuses a call without its own, which it reads as `MCP__SHAREDSECRET`; the agent holds them all as `MCP__SECRETS__<host>` and refuses to start without one for a server it dials. Anything you point at a server's `/mcp` by hand (an MCP inspector, a script) sends `Authorization: Bearer <that server's value>`.
- `REDIS_PASSWORD` — compose refuses to start the stack without it.
- `AGENTAPI__SHAREDSECRET` — `/api/agents` (custom-agent registration, routed by Caddy) refuses every verb without `Authorization: Bearer <value>`, and answers 400 without `X-Ziggurat-User: <user id>` (whose catalogue it is; a `userId` parameter is ignored).

## Accessing the WebChat & Dashboard

Caddy (port 443, Let's Encrypt TLS) is the entry point: `/hubs/*` → McpChannelSignalR, `/dashboard/*` → Observability, everything else → WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

The dashboard (an installable PWA) is at `https://assistants.herfluffness.com/dashboard/`, signed in as the operator. With the ports override `http://localhost:5003/` direct answers 401: the dashboard serves only the operator a verified `X-Ziggurat-Assertion` names, which only the session check signs (see below for asking it by hand).

## Accounts and signing in

`accounts` owns households, people, passkeys and sessions; Caddy routes `/accounts/*` on the main host to it (sign-in, a link's enrollment page, and the `/accounts/verify` session check). On a fresh store nobody can sign in until the operator mints their own link: `docker exec accounts dotnet Accounts.dll mint-link --operator --name "<display name>"` prints a one-time link (a week long) that enrolls a passkey; run it again after losing every passkey. `mint-link --user <id>` is a recovery link for anyone, `create-user --name <display name> [--role owner|member]` adds a person to the household (then give them a `mint-link --user`), and `mint-session --user <id>` prints a session cookie's value (`ziggurat_session`) for a test stack. The service signs who a turn is for (the session check's `X-Ziggurat-Assertion`, the internal resolution API's `assertion`) with a key it generates on first start into `./volumes/accounts/signing/`, publishing it on the compose network only at `http://accounts:8080/accounts/internal/jwks`. `docker exec accounts dotnet Accounts.dll rotate-signing-key` makes a new key the signing one while the old stays published; run `retire-signing-keys` once the old one's last assertion has run out (the rotation prints when: the assertion lifetime plus 30 seconds of skew, about 11 minutes). Losing the key costs nothing but a moment: assertions live ten minutes and none is stored. A test or eval stack sets `SIGNING__PRIVATEKEY` to `Tests/TestSigningKeys.Accounts` instead, and that key is neither rotated nor retired. A host run of the project (`Local: Accounts`, `http://localhost:5004`) uses `localhost` as the passkeys' relying party and a host-only cookie. The chat client and its hub are behind the session check, so they are reached through Caddy signed in; a host run of WebUI and the SignalR channel with no proxy in front has no session check to sign the person, and the hub refuses every connection. Every app behind the check (the hub, Sugary's API, the dashboard, and `/api/agents` beside its secret) believes only the signed `X-Ziggurat-Assertion`, verified against the accounts service's JWKS (`ACCOUNTS__JWKSURL`; a host run's `appsettings.Local.json` names `http://localhost:5004/accounts/internal/jwks`), never the plain `X-Ziggurat-User`/`-Tenant`/`-Roles`. To be somebody at an app's port directly, ask the check what the proxy would: `curl -si -H "Cookie: ziggurat_session=<mint-session value>" "http://localhost:5004/accounts/verify?channel=<sugary|dashboard|signalr|sexytime>"` and send the four `X-Ziggurat-*` headers it answers (the assertion lives ten minutes and names that channel only).

The agent signs every tool call, and the delegations a schedule's fire, a watch's fire and a login wake act on, with its own key in `./volumes/agent-keys`. Its verbs run beside the running agent, like the accounts service's: `docker exec agent dotnet Agent.dll rotate-signing-key` makes a new key the signing one (the agent and every server pick it up by themselves); `docker exec -it agent dotnet Agent.dll redelegate` then signs every stored delegation again with it — without asking, since the old key's still verify — and `retire-signing-keys` unpublishes the old key once its last call token has run out (the rotation prints when: two minutes plus 30 s). Retiring refuses while a stored delegation is still the old key's or a server that keeps them (scheduling, Home Assistant) cannot be asked; `--force` retires anyway, leaving those firing for nobody until `redelegate` is answered for them. A delegation lives as long as its object, so losing the key volume is the case that costs something: the agent generates a new key on start, every schedule and watch then fires for nobody, and `redelegate` shows each one as it claims to be (whose, and what it does now) and signs it again on `y` (`--yes` for all). The first deploy of delegations is the same: run `redelegate` once, and every schedule written before it — none has a delegation — is offered as the writer the household migration stamped on it; watches fired for nobody before and still do until a person's turn writes them again. A test or eval stack's agent signs with `SIGNING__PRIVATEKEY`, and every verb exits 1 having changed nothing.

## Migrating into the home household

The deploy that brings accounts runs `household-migration` once, by hand: it makes the retired identity map's people accounts and moves what was under their names — memories, remembered logins, phones, schedules' writers, topics, voice profiles, the Sugary log — onto their user ids. It is under the `migrate` profile, so no `up` starts it. The steps, in order, are `.scratch/accounts/spec.md`'s Further Notes; in short:

```bash
# people.json: { "people": [ { "name", "telegram", "telegramId", "webChat", "voice", "oldNames": [] }, ... ] }
#   name/telegram/webChat/voice are prod's IDENTITY__PEOPLE__<n>__* values; telegramId the numeric id.
mkdir -p DockerCompose/volumes/household-migration && cp people.json DockerCompose/volumes/household-migration/
# HOUSEHOLD_MIGRATION_SECRET=<openssl rand -hex 32> in DockerCompose/.env, then:
docker compose -f DockerCompose/docker-compose.yml -p jackbot up -d accounts
docker compose -f DockerCompose/docker-compose.yml -p jackbot --profile migrate run --rm household-migration \
  --people /input/people.json --operator <name> --owner <name> --sugary <name> --dry-run   # then without --dry-run
docker compose -f DockerCompose/docker-compose.yml -p jackbot restart mcp-channel-voice tse-extractor
# empty HOUSEHOLD_MIGRATION_SECRET again, and `up -d accounts`
```

Each `<name>` is a person's `name` in the file. The run prints a passkey link for each person it created; running it again changes nothing and says so. A bucket of memories under a name nobody is listed with is named in the output: add the name to its person's `oldNames` and run again. `.claude/rules/household-migration.md` has what each step does.

## The Sugary app

`mcp-sugary` serves the carb-counting PWA (`Sugary.Client`, built into the image) on every route but `/mcp`: `http://localhost:6017/` with the ports override. It has no sign-in of its own: Caddy fronts it at `https://sugary.herfluffness.com` behind the accounts service's session check, as the chat client is (the site refuses `/mcp` and `/accounts/internal/*`, routes `/accounts/*` so the app sends a signed-out person to sign in same-origin, and allows 230 MiB bodies for a filmed meal), which installing it to a phone and live barcode scanning need. The app's API believes only the signed `X-Ziggurat-Assertion` the check copies, for channel `sugary`, and answers 401 without one, so `localhost:6017` straight answers every `/api` call 401 — ask `/accounts/verify?channel=sugary` on 5004 with a session cookie (above) and send its headers to act as somebody. Each person's log is their own; foods and recipes are the household's. Port 6017 is published on loopback only, for local runs and the E2E. For frontend work, `npm run dev` in `Sugary.Client` proxies `/api` to that port the way Caddy does: start it with `SUGARY_SESSION=<mint-session value>` (a `create-user` person's) and it asks the accounts service's session check (`ACCOUNTS_URL`, default `http://localhost:5004`) before each call and copies the four headers onto it — without it every call is 401; `npm run build` writes into `McpServerSugary/wwwroot`, which `dotnet run` serves when started from the project directory. The client needs Node 22.12 or later: an older one installs without rolldown's native binding and vite cannot start.

## The smoke stack

`scripts/smoke-stack.sh up|test|down` runs the shipped compose file and Caddyfile on this machine as project `jackbot-smoke`, from empty state under `DockerCompose/volumes/smoke` (`docker-compose.override.smoke.yml`), with local secrets and Caddy on its own CA (`CADDY_TLS=internal`). It starts every server an agent dials but never Telegram, voice or Home Assistant itself, so a stale copy of the deployment's data cannot fire schedules, take the bot's updates or dial the satellites. `test` runs `Category=Smoke`, which reaches Caddy at 127.0.0.1 by the deployment's hostnames.

## Debugging with Playwright

Use `ignoreHTTPSErrors: true` for the browser context locally (the certificate is valid for `assistants.herfluffness.com`, not `localhost`). Every WebChat route is behind the session check, so a fresh browser lands on `/accounts/sign-in`. To drive it without a passkey ceremony, seed a person and a session (`docker exec accounts dotnet Accounts.dll create-user --name <n>`, then `mint-session --user <id>`) and add the printed value as the `ziggurat_session` cookie for the host you browse before opening the page.
