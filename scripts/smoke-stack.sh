#!/usr/bin/env bash
#
# A smoke stack on this machine: the deployment's own compose file and Caddyfile, run from empty state
# with local secrets, then checked from outside through Caddy as a browser would reach it.
#
#   scripts/smoke-stack.sh up      build and start it (project jackbot-smoke)
#   scripts/smoke-stack.sh test    run the Smoke tests against it (Category=Smoke)
#   scripts/smoke-stack.sh migrate --operator <n> --owner <n> --sugary <n> [--dry-run]
#                                  the household migration against it
#   scripts/smoke-stack.sh down    stop it and remove its state
#
# Everything here is local. The tests reach the stack on 127.0.0.1 alone, whatever the deployment's
# hostnames resolve to, and nothing starts that talks to the outside world on the household's
# behalf: no Telegram bot (it would take the deployment's updates). A household's own servers —
# web search, vault, library, sandbox, printer, Home Assistant, voice — are the tenant kit's
# (TenantKit/), not this file's, so none of them starts here: a turn goes without them and is told
# they are offline. Never point this at the deployment's host.

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

case "${1:-}" in
  up)
    mkdir -p DockerCompose/volumes/smoke/{redis,accounts,agent-keys,sugary,caddy_data,caddy_config}
    "${DC[@]}" build base-sdk
    "${DC[@]}" build "${SERVICES[@]}"
    "${DC[@]}" up -d --no-deps "${SERVICES[@]}"
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
