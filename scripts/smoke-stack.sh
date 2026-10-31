#!/usr/bin/env bash
#
# A smoke stack on this machine: the deployment's own compose file and Caddyfile, run from empty state
# with local secrets, then checked from outside through Caddy as a browser would reach it.
#
#   scripts/smoke-stack.sh up      build and start it (project jackbot-smoke), and the home's tenant kit
#                                  beside it (project tenant-kit-smoke)
#   scripts/smoke-stack.sh test    run the Smoke tests against it (Category=Smoke)
#   scripts/smoke-stack.sh migrate --operator <n> --owner <n> --sugary <n> [--dry-run]
#                                  the household migration against it
#   scripts/smoke-stack.sh down    stop both and remove their state
#
# Everything here is local. The tests reach the stack on 127.0.0.1 alone, whatever the deployment's
# hostnames resolve to, and nothing starts that talks to the outside world on the household's
# behalf: no Telegram bot (it would take the deployment's updates). A household's own servers are
# the tenant kit's (TenantKit/): up mints a tunnel credential for each from the smoke stack's own
# accounts service, for its home household, and starts the kit with them, its tunnels opened to this
# machine's Caddy by the deployment's main host (TenantKit/docker-compose.override.smoke.yml). Web
# search, the vault, the library, the sandbox and the printer start; voice, Home Assistant's server,
# Home Assistant and Music Assistant never do — they reach satellites, the home and the LAN — so a
# turn goes without them and is told they are offline. Never point this at the deployment's host.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) OS_OVERRIDE=DockerCompose/docker-compose.override.windows.yml ;;
  *) OS_OVERRIDE=DockerCompose/docker-compose.override.linux.yml ;;
esac

DC=(docker compose -f DockerCompose/docker-compose.yml -f "$OS_OVERRIDE"
    -f DockerCompose/docker-compose.override.smoke.yml -p jackbot-smoke)
[[ -e /dev/dri ]] || DC+=(-f DockerCompose/docker-compose.override.no-dri.yml)

# Every shared server an agent dials at a URL is here, since a session whose configured URL does not
# answer fails to build; a household's server it names is offline instead, never fatal.
SERVICES=(redis accounts-volume agent-volume mcp-sugary-volume accounts mcp-idealista mcp-channel-signalr
          mcp-scheduling mcp-timers mcp-sugary agent webui observability caddy)

# Local values for every secret the started servers ask for. The real ones come from the project's
# user secrets (mounted by the OS override), which win over these where both exist.
export REDIS_PASSWORD="${REDIS_PASSWORD:-local-redis-password}"
while read -r name; do export "$name=${!name:-local-mcp-secret}"; done \
  < <(grep -o '^MCP_SECRET_[A-Z_]*' DockerCompose/.env)
export AGENTAPI__SHAREDSECRET="${AGENTAPI__SHAREDSECRET:-local-agentapi-secret}"
export HOUSEHOLD_MIGRATION_SECRET="${HOUSEHOLD_MIGRATION_SECRET:-local-migration-secret}"

# The kit, as its own project: the tracked TenantKit/.env is read for everything left out here, and
# what is exported here wins over its empty placeholders.
KIT=(docker compose -f TenantKit/docker-compose.yml -f TenantKit/docker-compose.override.smoke.yml
     -p tenant-kit-smoke)
[[ -e /dev/dri ]] || KIT+=(-f TenantKit/docker-compose.override.no-dri.yml)
KIT_SERVERS=(websearch vault library sandbox printer)
KIT_SERVICES=(redis camoufox mcp-websearch mcp-vault mcp-library mcp-sandbox mcp-printer)
KIT_STATE=DockerCompose/volumes/smoke/kit
# The deployment's main host, as the Caddyfile serves it: where every kit server's tunnel opens.
DEPLOYMENT_HOST="$(grep -o '^assistants\.[^ ]*' DockerCompose/caddy/Caddyfile)"
export DEPLOYMENT_HOST
export KIT_REDIS_PASSWORD="${KIT_REDIS_PASSWORD:-local-kit-redis-password}"

# Asks until the command succeeds: the accounts service's verbs need its store prepared, and Caddy
# makes its local CA on its first start.
retry() {
  for _ in $(seq 60); do "$@" 2>/dev/null && return 0; sleep 2; done
  "$@"
}

# One section of the home household's configuration as the household migration seeds it
# (HouseholdMigration/Home): the agent's settings carry no agents, so without them the home would run
# the two templates, and the signed-in reply reads a vault note through the home's own jonas. Saving
# again on a second up changes nothing. No master key is set here, and these sections hold no secret.
seed() {
  docker exec -i accounts dotnet Accounts.dll set-household-config --household home --section "$1" --file - \
    < "HouseholdMigration/Home/$1.json"
}

# One credential per kit server for the home household, minted once per smoke state and kept beside it
# (credentials.env), so a second up starts the kit with the same ones.
credentials() {
  if [[ ! -s "$KIT_STATE/credentials.env" ]]; then
    local made home
    made="$(retry docker exec accounts dotnet Accounts.dll create-user --name "Smoke Kit")"
    home="$(grep -o 'household [0-9a-f]\{32\}' <<<"$made" | cut -d' ' -f2)"
    [[ -n "$home" ]] || { echo "no household in: $made" >&2; exit 1; }
    local server minted
    : > "$KIT_STATE/credentials.env.new"
    for server in "${KIT_SERVERS[@]}"; do
      # The credential is the last line, alone, by the command's contract.
      minted="$(docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household "$home" --server "$server")"
      echo "TUNNEL_CREDENTIAL_${server^^}=$(tail -n 1 <<<"$minted")" >> "$KIT_STATE/credentials.env.new"
    done
    mv "$KIT_STATE/credentials.env.new" "$KIT_STATE/credentials.env"
  fi
  set -a
  # shellcheck disable=SC1091
  source "$KIT_STATE/credentials.env"
  set +a
}

case "${1:-}" in
  up)
    mkdir -p DockerCompose/volumes/smoke/{redis,accounts,agent-keys,sugary,caddy_data,caddy_config}
    "${DC[@]}" build base-sdk
    "${DC[@]}" build "${SERVICES[@]}"
    "${DC[@]}" up -d --no-deps "${SERVICES[@]}" smoke-probe

    mkdir -p "$KIT_STATE"/{redis,vault,media,sandbox}
    credentials
    retry seed agents
    retry seed features
    # Caddy's local root, which the kit's servers are told to trust for the hub; copied out because
    # Caddy keeps it where only root reads.
    retry docker exec caddy test -s /data/caddy/pki/authorities/local/root.crt
    docker exec caddy cat /data/caddy/pki/authorities/local/root.crt > "$KIT_STATE/hub-ca.crt"
    # base-sdk is the deployment's, built above.
    "${KIT[@]}" build "${KIT_SERVICES[@]}"
    "${KIT[@]}" up -d --no-deps "${KIT_SERVICES[@]}"
    ;;
  test)
    ZIGGURAT_SMOKE=1 dotnet test Tests/Tests.csproj --filter "Category=Smoke"
    ;;
  migrate)
    # The household migration against the smoke stack, its people file in
    # DockerCompose/volumes/smoke/household-migration/people.json; the rest of the arguments are its.
    shift
    "${DC[@]}" --profile migrate build household-migration
    "${DC[@]}" --profile migrate run --rm --no-deps household-migration --people /input/people.json "$@"
    ;;
  down)
    "${KIT[@]}" down --volumes --remove-orphans
    "${DC[@]}" down --volumes --remove-orphans
    # The containers wrote as their own users; a throwaway container removes what they wrote. Only
    # where there is something: bind-mounting a missing ./volumes makes Docker create it as root,
    # and the next up could then not make its directories.
    if [[ -d DockerCompose/volumes/smoke ]]; then
      docker run --rm -v "$ROOT/DockerCompose/volumes:/v" alpine rm -rf /v/smoke
    fi
    ;;
  *)
    echo "usage: $0 up|test|migrate|down" >&2
    exit 2
    ;;
esac
