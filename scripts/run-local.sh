#!/usr/bin/env bash
#
# Run Ziggurat services on the host, outside Docker.
#
# This exists because Zed has no compound debug configurations. You run the
# services you are not debugging with this script, and start the one or two you
# are debugging from .zed/debug.json. The port and environment table below is
# the same one .zed/debug.json uses, so the two stay interchangeable.
#
#   scripts/run-local.sh                  run every service
#   scripts/run-local.sh agent webui      run only those services
#   SKIP="agent" scripts/run-local.sh     run every service except agent
#
# A service whose port is already listening is skipped, so starting a debug
# session first and this script second does the right thing without any flags.
#
# Requires a prior build: dotnet build Ziggurat.sln -c Debug

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${CONFIG:-Debug}"
TFM="${TFM:-net10.0}"
ENV_FILE="$ROOT/DockerCompose/.env"

# Every MCP server's /mcp asks for the deployment secret and the agent presents it, so every
# service started here gets the same value — the one in the environment if there is one, else a
# fixed local one. It is passed after .env is sourced, so an empty placeholder there cannot win.
MCP_SECRET="${MCP__SHAREDSECRET:-local-mcp-secret}"

# name|project directory|port|set ASPNETCORE_ENVIRONMENT|load DockerCompose/.env|extra env (; separated)|program args (space separated)
SERVICES=(
  "agent|Agent|5000|yes|no||--chat Web --reasoning"
  "webui|WebChat|5001|yes|no||"
  "observability|Observability|5003|yes|no||"
  "library|McpServerLibrary|6001|no|no||"
  "vault|McpServerVault|6002|no|no||"
  "websearch|McpServerWebSearch|6003|no|no||"
  "sandbox|McpServerSandbox|6004|no|no||"
  "idealista|McpServerIdealista|6005|no|no||"
  "homeassistant|McpServerHomeAssistant|6006|no|no||"
  "signalr|McpChannelSignalR|6010|yes|no||"
  "telegram|McpChannelTelegram|6011|no|no||"
  "servicebus|McpChannelServiceBus|6012|no|no||"
  "scheduling|McpServerScheduling|6013|no|no|RedisConnectionString=localhost:6379|"
  "printer|McpServerPrinter|6014|no|no|SPOOLPATH=$ROOT/McpServerPrinter/.spool|"
  "voice|McpChannelVoice|6015|yes|yes|RedisConnectionString=localhost:6379;Satellites__kitchen-01__Identity=household;Satellites__kitchen-01__Room=Kitchen;Satellites__kitchen-01__WakeWord=hey_jarvis;Satellites__kitchen-01__Address=tcp://127.0.0.1:10800|"
  "timers|McpServerTimers|6016|no|yes|VoiceHub__BaseUrl=http://localhost:6015|"
)

# Reads the kernel socket table rather than dialling the port. A connect probe
# is slow under WSL2 and cannot tell "refused" from "filtered".
port_is_taken() {
  command -v ss >/dev/null 2>&1 || return 1
  [[ -n "$(ss -H -ltn "sport = :$1" 2>/dev/null)" ]]
}

pids=()

shut_down() {
  trap - INT TERM EXIT
  local pid
  for pid in "${pids[@]:-}"; do
    [[ -n $pid ]] && kill "$pid" 2>/dev/null
  done
  wait 2>/dev/null
}

start_service() {
  local name=$1 dir=$2 port=$3 aspnet_env=$4 use_env_file=$5 extra=$6 prog_args=$7
  local dll="$ROOT/$dir/bin/$CONFIG/$TFM/$dir.dll"

  if [[ ! -f $dll ]]; then
    echo "[$name] no build at $dll — run: dotnet build Ziggurat.sln -c $CONFIG" >&2
    return 1
  fi

  # The service reports its own pid: $! would name the trailing sed, and killing
  # that leaves the service running.
  local pidfile
  pidfile="$(mktemp)"

  (
    cd "$ROOT/$dir" || exit 1
    echo "$BASHPID" >"$pidfile"

    if [[ $use_env_file == yes ]]; then
      if [[ -f $ENV_FILE ]]; then
        set -a
        # shellcheck disable=SC1090
        source "$ENV_FILE"
        set +a
      else
        echo "[$name] $ENV_FILE is missing; starting without it" >&2
      fi
    fi

    # Passed through env(1) rather than export: configuration keys carry the
    # satellite id, as in Satellites__kitchen-01__Room, and the hyphen makes
    # that an invalid shell identifier. env has no such restriction, and .NET
    # reads it the same way either way.
    local -a envs=(
      "DOTNET_ENVIRONMENT=Local"
      "ASPNETCORE_URLS=http://localhost:$port"
      "MCP__SHAREDSECRET=$MCP_SECRET"
    )
    [[ $aspnet_env == yes ]] && envs+=("ASPNETCORE_ENVIRONMENT=Local")

    if [[ -n $extra ]]; then
      local pair
      while IFS= read -r pair; do
        [[ -n $pair ]] && envs+=("$pair")
      done <<<"${extra//;/$'\n'}"
    fi

    # shellcheck disable=SC2086
    exec env "${envs[@]}" dotnet "$dll" $prog_args
  ) 2>&1 | sed -u "s/^/[$name] /" &

  pids+=("$!")

  local waited=0
  while [[ ! -s $pidfile && $waited -lt 50 ]]; do
    sleep 0.1
    waited=$((waited + 1))
  done
  [[ -s $pidfile ]] && pids+=("$(<"$pidfile")")
  rm -f "$pidfile"

  echo "[$name] started on http://localhost:$port"
}

main() {
  local requested=("$@")
  local skip=" ${SKIP:-} "
  local started=0

  trap shut_down INT TERM EXIT

  local row name dir port aspnet_env use_env_file extra prog_args
  for row in "${SERVICES[@]}"; do
    IFS='|' read -r name dir port aspnet_env use_env_file extra prog_args <<<"$row"

    if [[ ${#requested[@]} -gt 0 ]] && [[ ! " ${requested[*]} " == *" $name "* ]]; then
      continue
    fi

    if [[ $skip == *" $name "* ]]; then
      echo "[$name] skipped (SKIP)"
      continue
    fi

    if port_is_taken "$port"; then
      echo "[$name] skipped — port $port is already in use"
      continue
    fi

    start_service "$name" "$dir" "$port" "$aspnet_env" "$use_env_file" "$extra" "$prog_args" &&
      started=$((started + 1))
  done

  if [[ ${#requested[@]} -gt 0 ]]; then
    for name in "${requested[@]}"; do
      [[ " ${SERVICES[*]} " == *"${name}|"* ]] || echo "unknown service: $name" >&2
    done
  fi

  if [[ $started -eq 0 ]]; then
    echo "nothing to run" >&2
    shut_down
    exit 1
  fi

  echo "$started service(s) running — Ctrl-C to stop them all"
  wait
}

main "$@"
