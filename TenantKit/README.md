# The tenant kit

A household's own servers, run at the household's own site: web search with its browser (Camoufox),
the vault, the library with qBittorrent and Jackett, the sandbox, the printer, the Home Assistant
server with Home Assistant and Music Assistant, and voice with speech extraction — the same images
the deployment builds — plus the kit's own Redis and the household's LAN tools over its media, Plex
and filebrowser.

Each server opens **one outbound tunnel** to the deployment (`wss://<main host>/tunnel`, the proxy's
one route for it) and is reached through it and nothing else: the agent's calls, a channel's
notifications, the browse pages a person opens from a link and the timers server's rings come in
over it, and the few things a server asks of the deployment — voice's speakers, topics and speech,
the sandbox's bridge to the session's other mounts, the metrics feed — go back over it as
**back-calls** from a port on the server's own loopback. The kit's networks are its own; the machine
needs no open port; and the kit holds **no deployment secret** — each server holds the tunnel
credential the deployment's operator minted for it, and the rest of `.env` is the household's own.
`.claude/rules/tunnel.md` is the contract; `Tests/Unit/Deployment/TenantKitConformanceTests.cs` holds
this file to it.

| Server | Tunnel name | Back-calls | Beside it |
|---|---|---|---|
| `mcp-websearch` | `websearch` | metrics | `camoufox`, the kit's `redis` |
| `mcp-vault` | `vault` | | |
| `mcp-library` | `library` | | `qbittorrent`, `jackett`, the kit's `redis` |
| `mcp-sandbox` | `sandbox` | vfs-bridge | a network of its own |
| `mcp-printer` | `printer` | | |
| `mcp-homeassistant` | `homeassistant` | | `homeassistant`, `music-assistant` |
| `mcp-channel-voice` | `voice` | accounts, topics, lemonade, metrics | `tse-extractor` |

Plex (on the host's network, with the render node) and filebrowser (`${LAN_BIND_ADDRESS}:8002`) are
no servers: nothing reaches them through the deployment, only the household's own LAN.

The agent's definitions name these servers by their tunnel names (`Agent/appsettings.json`), and the
deployment finds two of them by name: `websearch` for the browse pages and `voice` for the timers'
rings (`HouseholdServerNames`). A server that is not connected is left out of a turn, which is told
it is offline; nothing fails.

## Running it

1. **Credentials.** The deployment's operator mints one per server, for the household it belongs to,
   and each is printed once:

   ```bash
   for server in websearch vault library sandbox printer homeassistant voice; do
     docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household <household id> --server $server
   done
   ```

   Each goes in `.env` as `TUNNEL_CREDENTIAL_<SERVER>`. `list-tunnel-credentials` shows them without
   their secrets and `revoke-tunnel-credential --id <id>` closes that server's tunnel at once.

2. **The rest of `.env`**: `DEPLOYMENT_HOST` (the deployment's main host — the hub, and where a login
   handoff's link points), `KIT_REDIS_PASSWORD`, `LOGINS__STATEKEY`, `Announce__Token`, the API keys
   and tokens, and `PUID`/`PGID`/`RENDER_GID`/`LAN_BIND_ADDRESS`. Every line says what it is.
   `STT_MODEL` must be the deployment's: voice asks the deployment's Lemonade for that model.

3. **Start it**, from the repository root (base-sdk first: every image builds from it, and `up
   --build` reuses a stale one):

   ```bash
   docker compose -f TenantKit/docker-compose.yml build base-sdk
   docker compose -f TenantKit/docker-compose.yml up -d --build
   ```

   Add `-f TenantKit/docker-compose.override.no-dri.yml` on a host with no `/dev/dri` (it strips the
   sandbox's and Plex's render node), or
   `.rocm.yml` for ROCm in the sandbox. The sandbox needs the host's `ziggurat-sandbox` AppArmor
   profile installed first (`DockerCompose/apparmor/`), as on the deployment.

4. **Check** from the deployment: `docker exec agent dotnet Agent.dll list-household-servers
   --household <household id>` lists each connected server, since when, and the tools it offers
   through its tunnel. A server's own log says why a tunnel was refused (the relay's reason:
   `Unknown`, `Revoked`, `AnotherServer`).

### Reaching the hub

The servers dial `wss://${DEPLOYMENT_HOST}/tunnel` as anyone on the internet would. On the machine
the deployment itself runs on, that name may resolve to the public address and the router may not
send it back in (no hairpin NAT); then point the name at the host for the kit's containers with an
override giving each tunnelled server `extra_hosts: ["<main host>:host-gateway"]`.

A deployment served by a certificate the system's roots do not trust — a smoke stack on Caddy's own
local CA — is trusted by naming that CA's certificate (PEM) to each server:
`MCP__TUNNEL__HUBCERTIFICATEAUTHORITY=<path inside the container>`, with the file mounted there. The
hub's certificate is then accepted if it chains to the system's roots or to that one; its name must
still match. `docker-compose.override.smoke.yml` is both at once, for the smoke stack
(`scripts/smoke-stack.sh`, which starts the kit beside it as project `tenant-kit-smoke`).

## Moving the home onto the tunnel

The deployment used to run these servers itself. Moving them is one deploy, in this order. The
moved services must be gone from the deployment before the kit starts: two voice servers would dial
the same satellites, two Home Assistants would share one configuration, and the LAN tools, Plex and
Music Assistant would fight over their ports.

0. **The household migration first**, if the deploy that brought accounts has not run it
   (`.claude/skills/launch-stack/SKILL.md`, "Migrating into the home household"): it re-seals the
   remembered logins and renames the voice profiles where the deployment keeps them, and the steps
   below carry what it left.

1. **Mint a credential per home server** (above, "Running it" 1). The home household's id is the
   `household` that `docker exec accounts dotnet Accounts.dll mint-link --user <your user id>`
   prints, or the `X-Ziggurat-Tenant` the session check answers you.

2. **Remove the moved services from the deployment** and reload the proxy for the tunnel's route:

   ```bash
   # with the override files the deployment runs with (the OS's user secrets, ...)
   docker compose -f DockerCompose/docker-compose.yml -p jackbot up -d --build --remove-orphans
   docker exec caddy caddy reload --config /etc/caddy/Caddyfile
   ```

   `--remove-orphans` removes the containers the new file no longer names (`mcp-websearch`,
   `mcp-vault`, `mcp-library`, `mcp-sandbox`, `mcp-printer`, `mcp-homeassistant`,
   `mcp-channel-voice`, `camoufox`, `qbittorrent`, `jackett`, `homeassistant`, `music-assistant`,
   `tse-extractor`, `plex`, `filebrowser`); their volumes stay. From here until the kit is up, turns go without the home's
   servers and say so.

3. **Carry the volumes.** Bind mounts are folders: copy each from `DockerCompose/volumes/` to
   `TenantKit/volumes/` with its owner kept (`sudo cp -a`), or point the kit's `_PATH` settings at
   the folders where they are.

   | What | From (`DockerCompose/`) | To (`TenantKit/`) |
   |---|---|---|
   | Home Assistant | `volumes/homeassistant_config` | `volumes/homeassistant_config` |
   | Music Assistant | `volumes/music-assistant-data` | `volumes/music-assistant-data` |
   | voice profiles, audit | `volumes/voices`, `volumes/tse-audit` | the same |
   | speech extraction's models | `volumes/tse-models` | `volumes/tse-models` |
   | library's tools | `volumes/qbittorrent_config`, `volumes/jackett_config`, `volumes/jackett_data` | the same |
   | Plex | `volumes/plex_config` | `volumes/plex_config` |
   | filebrowser | `volumes/filebrowser_database`, `volumes/filebrowser_config` | the same |
   | the media | `DATA_PATH` | `DATA_PATH` (the deployment's `.env` no longer names it) |
   | vault | `VAULT_PATH` | `VAULT_PATH` |
   | sandbox home | `SANDBOX_HOME_PATH` | `SANDBOX_HOME_PATH` |

   Two are named volumes, the browser's identity and the printer's spool; they are carried once the
   kit has made its own (step 4).

4. **Start the kit** ("Running it" 2 and 3). `LOGINS__STATEKEY` must be the value the deployment's
   `.env` holds — the remembered logins move sealed — and `Announce__Token` the deployment's old one,
   or Home Assistant's `rest_command` for watches must be given the new one. Then carry the two named
   volumes into the ones the kit made, with their services stopped, and start them again:

   ```bash
   docker compose -f TenantKit/docker-compose.yml stop mcp-websearch camoufox mcp-printer
   docker run --rm -v jackbot_camoufox-identity:/from -v tenant-kit_camoufox-identity:/to alpine sh -c 'cp -a /from/. /to/'
   docker run --rm -v jackbot_printer-spool:/from -v tenant-kit_printer-spool:/to alpine sh -c 'cp -a /from/. /to/'
   docker compose -f TenantKit/docker-compose.yml up -d
   ```

   The browser's identity is its one fingerprint: carried over, the sites people are logged into see
   the same device.

5. **Run the state move**: the remembered logins, the one-time links and the library's download
   routing, out of the deployment's Redis and into the kit's. It reaches both on the host's loopback
   — the deployment's on its published port (or `ssh -L 6379:127.0.0.1:6379 <deployment host>` from
   another machine) — and is told the deployment's password for that run alone:

   ```bash
   docker compose -f TenantKit/docker-compose.yml --profile move build state-move
   docker compose -f TenantKit/docker-compose.yml --profile move run --rm \
     -e HOMESTATE__FROM="127.0.0.1:6379,password=<the deployment's REDIS_PASSWORD>" state-move --dry-run
   # then the same without --dry-run
   ```

   It says what it moved, by kind. Running it again moves nothing twice: a key already in the kit's
   Redis is left as it is, so a login a person made through the kit since is never overwritten, and
   the download routing's index is merged. It copies; the deployment's Redis keeps its copy, which
   nothing there reads any more.

6. **Check** ("Running it" 4): all seven servers listed, a turn that uses one of them, a login
   handoff's link opening the live view.

7. **Clean the deployment's `.env`** of what it no longer reads: the moved servers' `MCP_SECRET_*`,
   the Home Assistant and Music Assistant tokens, qBittorrent, Jackett, Brave and CapSolver, and
   `Announce__Token`. `LOGINS__STATEKEY` stays only while the household migration may still run.
