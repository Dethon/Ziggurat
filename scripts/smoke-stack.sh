#!/usr/bin/env bash
#
# A smoke stack on this machine: the deployment's own compose file and Caddyfile, run from empty state
# with local secrets, then checked from outside through Caddy as a browser would reach it.
#
#   scripts/smoke-stack.sh up      build and start it (project jackbot-smoke), run the household migration
#                                  into it as the deploy does, and start the home's tenant kit beside it
#                                  (project tenant-kit-smoke); Lemonade starts too, for memory's
#                                  embeddings, on the NVIDIA card where Docker has the runtime
#   scripts/smoke-stack.sh test    run the Smoke tests against it (Category=Smoke)
#   scripts/smoke-stack.sh migrate --operator <n> --owner <n> --sugary <n> [--dry-run]
#                                  the household migration against it
#   scripts/smoke-stack.sh voice   the kit's voice server and a test satellite beside the running stack:
#                                  two questions spoken to it (a sum, a one-minute timer), the answers
#                                  heard back and the timer ringing on it, checked; Lemonade starts for
#                                  it, on the CPU unless STT_BACKEND says otherwise
#   scripts/smoke-stack.sh household-vault
#                                  (re)start a second household's vault beside the kit's, with the
#                                  tunnel credential in SMOKE_HOUSEHOLD_VAULT_CREDENTIAL (the smoke
#                                  test that onboards a household runs it)
#   scripts/smoke-stack.sh household-lemonade
#                                  (re)start a household's Lemonade forwarder in front of a stand-in
#                                  box offering one model, with the tunnel credential in
#                                  SMOKE_HOUSEHOLD_LEMONADE_CREDENTIAL (the smoke test that sets up a
#                                  household's kit box runs it)
#   scripts/smoke-stack.sh household-voice <household> [refused]
#                                  a second household's voice server and a test satellite of its own
#                                  beside the running stack, with the voice credential in
#                                  SMOKE_HOUSEHOLD_VOICE_CREDENTIAL: once the household's tunnel is
#                                  open, the satellite asks the sum and the one-minute timer, and the
#                                  answer and the ring are checked; with refused (a household with no
#                                  voice agent) it asks the sum alone and listens for the refusal. The
#                                  smoke tests that onboard a household with voice run it
#   scripts/smoke-stack.sh down    stop both and remove their state
#
# Everything here is local. The tests reach the stack on 127.0.0.1 alone, whatever the deployment's
# hostnames resolve to, and nothing starts that talks to the outside world on the household's
# behalf: no Telegram bot (it would take the deployment's updates). A household's own servers are
# the tenant kit's (TenantKit/): up mints a tunnel credential for each from the smoke stack's own
# accounts service, for its home household, and starts the kit with them, its tunnels opened to this
# machine's Caddy by the deployment's main host (TenantKit/docker-compose.override.smoke.yml). Web
# search, the vault, the library, the sandbox and the printer start, and a machine running the outpost
# binary published from this checkout (smoke-machine), its own credential minted beside theirs; Home Assistant's server, Home
# Assistant and Music Assistant never do — they reach the home and the LAN — so a turn goes without
# them and is told they are offline. Voice starts only for `voice`, dialling a test satellite alone. A household an owner keys here has its keys
# sealed with a master key made for this smoke state, and probed against a stub (smoke-keys) that takes
# every key but one with "refused" or "unreachable" in it, so no key is sent to a provider to be checked. Never point this at the deployment's host.

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
# Lemonade on the machine's NVIDIA card where Docker has the runtime: its embeddings on CUDA, since
# under WSL the image's Vulkan sees only llvmpipe and memory's embeddings timed out on the CPU under a
# few turns at once. Whisper stays on the CPU there (docker-compose.override.nvidia.yml says why).
if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q '"nvidia"'; then
  DC+=(-f DockerCompose/docker-compose.override.nvidia.yml)
fi

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

# The household configuration's master key, so an owner's key can be saved here: made once per smoke
# state and kept beside it, since a second up with another would leave every sealed secret unreadable.
# Never one from the environment, which could be the deployment's.
MASTER_KEY=DockerCompose/volumes/smoke/household-config-master.key
HOUSEHOLD_CONFIG_MASTER_KEY="$( [[ -s "$MASTER_KEY" ]] && cat "$MASTER_KEY" || true)"
export HOUSEHOLD_CONFIG_MASTER_KEY

# The kit, as its own project: the tracked TenantKit/.env is read for everything left out here, and
# what is exported here wins over its empty placeholders. Its servers run as whoever runs this, who
# makes their folders below (the vault, the media, the sandbox's): left to the kit's default user they
# could read those folders and write nothing, as a deployment whose .env names no PUID would.
KIT=(env PUID="$(id -u)" PGID="$(id -g)"
     docker compose -f TenantKit/docker-compose.yml -f TenantKit/docker-compose.override.smoke.yml
     -p tenant-kit-smoke)
[[ -e /dev/dri ]] || KIT+=(-f TenantKit/docker-compose.override.no-dri.yml)
KIT_SERVERS=(websearch vault library sandbox printer)
KIT_SERVICES=(redis camoufox mcp-websearch mcp-vault mcp-library mcp-sandbox smoke-printer mcp-printer smoke-machine)
# The outpost the kit's machine runs, by the name its credential is minted for (outpost-<name>).
OUTPOST_NAME=smoke-machine
KIT_STATE=DockerCompose/volumes/smoke/kit
# The deployment's main host, as the Caddyfile serves it: where every kit server's tunnel opens.
DEPLOYMENT_HOST="$(grep -o '^assistants\.[^ ]*' DockerCompose/caddy/Caddyfile)"
export DEPLOYMENT_HOST
export KIT_REDIS_PASSWORD="${KIT_REDIS_PASSWORD:-local-kit-redis-password}"
# The kit asks for its site's printer; the smoke override replaces it with its stand-in, so this only
# fills the kit's required placeholder and never names a printer anything reaches.
export PRINTER_URI="${PRINTER_URI:-ipp://smoke-printer.local:8631/ipp/print}"

# The kit's web search asks Brave, and CapSolver for a challenge, with the keys a developer keeps in the
# web search server's user secrets for local testing — read here because the kit's containers mount no
# user secrets. Without them every web_search on this stack is refused, which is a stack that cannot be
# driven by hand. Left empty where there are none (or no dotnet), and never printed.
user_secret() {
  command -v dotnet > /dev/null || return 0
  dotnet user-secrets list --project "$1" 2>/dev/null | sed -n "s/^$2 = //p"
}
BRAVESEARCH__APIKEY="${BRAVESEARCH__APIKEY:-$(user_secret McpServerWebSearch braveSearch:apiKey)}"
CAPSOLVER__APIKEY="${CAPSOLVER__APIKEY:-$(user_secret McpServerWebSearch capSolver:apiKey)}"
export BRAVESEARCH__APIKEY CAPSOLVER__APIKEY

# Asks until the command succeeds: the accounts service's verbs need its store prepared, and Caddy
# makes its local CA on its first start.
retry() {
  for _ in $(seq 60); do "$@" 2>/dev/null && return 0; sleep 2; done
  "$@"
}

# The household migration as the deploy runs it (.scratch/tenants-config/deploy.md, steps 5 to 8), so
# what the tests ask is a stack the deploy path made: the operator signs in first, then the run carries
# the people into the home household, seeds the home's settings, agents, features and spaces
# (HouseholdMigration/Home), moves the shared stores into the household and stamps the schedules'
# writers; then the agent restarts, building every conversation from the seeded agents, and offers each
# schedule as its writer. No keys are piped in: the home's turns are paid with the deployment's key,
# and a Telegram bot would take the deployment's updates. The one person in
# the file is the operator, the owner and Sugary's. On the first up the stores also hold one schedule
# as it was kept before households, due long after any run, which the migration moves into the home;
# a second up runs it again and it changes nothing.
MIGRATION=DockerCompose/volumes/smoke/household-migration
BEFORE_HOUSEHOLDS=smoke-before-households
WRITTEN_IN_A_TOPIC=smoke-written-in-a-topic
migrate_home() {
  retry docker exec accounts dotnet Accounts.dll mint-link --operator --name "Smoke Operator" > /dev/null
  if [[ ! -s "$MIGRATION/people.json" ]]; then
    # The shape BeforeHouseholds.ScheduleAsync writes: schedule:{id}, the schedules set, and its place
    # in schedules:due by NextRunAt's ticks; no writer recorded, so the migration stamps the owner.
    docker exec redis redis-cli SET "schedule:$BEFORE_HOUSEHOLDS" \
      "{\"Id\":\"$BEFORE_HOUSEHOLDS\",\"AgentId\":\"jonas\",\"Prompt\":\"Smoke: a schedule from before households.\",\"RunAt\":\"2099-01-01T08:00:00Z\",\"NextRunAt\":\"2099-01-01T08:00:00Z\",\"CreatedAt\":\"2026-10-01T08:00:00Z\"}" > /dev/null
    docker exec redis redis-cli SADD schedules "$BEFORE_HOUSEHOLDS" > /dev/null
    docker exec redis redis-cli ZADD schedules:due 662065344000000000 "$BEFORE_HOUSEHOLDS" > /dev/null
    # A second, as a deployment from before principals kept every schedule: no writer recorded, but
    # the topic that wrote it holds the schedule mount's answer after its writer's message, so the
    # migration gives it to that person (smoke-writer) and not to the owner.
    docker exec redis redis-cli SET "schedule:$WRITTEN_IN_A_TOPIC" \
      "{\"Id\":\"$WRITTEN_IN_A_TOPIC\",\"AgentId\":\"jonas\",\"Prompt\":\"Smoke: a schedule a person wrote before principals.\",\"RunAt\":\"2099-01-01T08:00:00Z\",\"NextRunAt\":\"2099-01-01T08:00:00Z\",\"CreatedAt\":\"2026-10-01T08:00:00Z\"}" > /dev/null
    docker exec redis redis-cli SADD schedules "$WRITTEN_IN_A_TOPIC" > /dev/null
    docker exec redis redis-cli ZADD schedules:due 662065344000000000 "$WRITTEN_IN_A_TOPIC" > /dev/null
    docker exec redis redis-cli SET "topic:jonas:7001:smoke-writers-topic" \
      '{"TopicId":"smoke-writers-topic","ChatId":7001,"ThreadId":7002,"AgentId":"jonas","Name":"Smoke: a topic from before households","CreatedAt":"2026-10-01T08:00:00+00:00","LastMessageAt":"2026-10-01T08:00:00+00:00","SpaceSlug":"default"}' > /dev/null
    docker exec redis redis-cli SET "topic-of:jonas:7001:7002" smoke-writers-topic > /dev/null
    docker exec redis redis-cli ZADD "topics:jonas:default" 1790000000000 "7001:smoke-writers-topic" > /dev/null
    docker exec redis redis-cli HSET "topicdoc:jonas:7001:smoke-writers-topic" topicId smoke-writers-topic chatId 7001 \
      agentId jonas space default name "Smoke: a topic from before households" score 1790000000000 > /dev/null
    docker exec redis redis-cli RPUSH "agent-key:jonas:7001:7002" \
      '{"Role":"user","Contents":[{"$type":"text","Text":"Remind me on the first of 2099"}],"AdditionalProperties":{"SenderId":"smoke-writer"}}' \
      "{\"Role\":\"tool\",\"Contents\":[{\"\$type\":\"functionResult\",\"CallId\":\"smoke-call\",\"Result\":{\"status\":\"created\",\"filePath\":\"/schedules/jonas/$WRITTEN_IN_A_TOPIC/schedule.json\"}}],\"AdditionalProperties\":{\"Timestamp\":\"2026-10-01T08:00:00+00:00\"}}" > /dev/null
    echo '{ "people": [ { "name": "smoke", "displayName": "Smoke Operator" }, { "name": "smoke-writer", "displayName": "Smoke Writer" } ] }' > "$MIGRATION/people.json"
  fi
  "${DC[@]}" --profile migrate build household-migration
  "${DC[@]}" --profile migrate run --rm --no-deps household-migration --people /input/people.json \
    --operator smoke --owner smoke --sugary smoke
  "${DC[@]}" restart agent
  retry redelegated
}

# Redelegate asks every channel server the agent dials, and the ones this stack never starts (Telegram,
# voice, Home Assistant's) cannot be asked, so its exit code always says so. What has to have answered
# is the scheduling server, which keeps the schedules; no server named at all is the agent still
# restarting.
redelegated() {
  local said
  said="$(docker exec agent dotnet Agent.dll redelegate --yes 2>&1)" || true
  echo "$said"
  grep -q '^[a-z-]*: ' <<<"$said" && ! grep -q '^scheduling: could not be asked' <<<"$said"
}

# Whether a household's voice tunnel is open, as the agent's own listing says. Read whole before it is
# searched, so the search ending early never fails the listing under pipefail.
voice_open() {
  local listed
  listed="$(docker exec agent dotnet Agent.dll list-household-servers --household "$1")"
  grep -q '^voice  connected since' <<<"$listed"
}

# One credential per kit server for the home household, minted once per smoke state and kept beside it
# (credentials.env), so a second up starts the kit with the same ones.
credentials() {
  if [[ ! -s "$KIT_STATE/credentials.env" ]]; then
    local made home
    # The home household is the operator's: asked of the operator's link, which makes the operator on
    # a first up (migrate_home asks again under the same name), so no stand-in person is created only
    # to read a household id off and left listed among the home's members.
    made="$(retry docker exec accounts dotnet Accounts.dll mint-link --operator --name "Smoke Operator")"
    home="$(grep -o 'household [0-9a-f]\{32\}' <<<"$made" | cut -d' ' -f2)"
    [[ -n "$home" ]] || { echo "no household in: $made" >&2; exit 1; }
    local server minted
    : > "$KIT_STATE/credentials.env.new"
    for server in "${KIT_SERVERS[@]}"; do
      # The credential is the last line, alone, by the command's contract.
      minted="$(docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household "$home" --server "$server")"
      echo "TUNNEL_CREDENTIAL_${server^^}=$(tail -n 1 <<<"$minted")" >> "$KIT_STATE/credentials.env.new"
    done
    minted="$(docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household "$home" --server "outpost-$OUTPOST_NAME")"
    echo "TUNNEL_CREDENTIAL_OUTPOST=$(tail -n 1 <<<"$minted")" >> "$KIT_STATE/credentials.env.new"
    echo "$home" > "$KIT_STATE/household"
    mv "$KIT_STATE/credentials.env.new" "$KIT_STATE/credentials.env"
  fi
  # Voice's, minted the first time it is asked for, for the home household its other credentials were
  # minted for, as kept beside them. Never found by a server name: the smoke test that onboards a
  # household mints that household a vault too, and voice found by "the household with a vault" was
  # then that household's, so the home's agents never heard the satellite.
  if [[ "${1:-}" == voice ]] && ! grep -q '^TUNNEL_CREDENTIAL_VOICE=' "$KIT_STATE/credentials.env"; then
    [[ -s "$KIT_STATE/household" ]] \
      || { echo "no $KIT_STATE/household: this smoke state predates it; run down and up again" >&2; exit 1; }
    echo "TUNNEL_CREDENTIAL_VOICE=$(docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household "$(cat "$KIT_STATE/household")" --server voice | tail -n 1)" \
      >> "$KIT_STATE/credentials.env"
  fi
  set -a
  # shellcheck disable=SC1091
  source "$KIT_STATE/credentials.env"
  set +a
}

case "${1:-}" in
  up)
    mkdir -p DockerCompose/volumes/smoke/{redis,accounts,agent-keys,sugary,voices,household-migration,caddy_data,caddy_config}
    "${DC[@]}" build base-sdk
    "${DC[@]}" build "${SERVICES[@]}"
    if [[ ! -s "$MASTER_KEY" ]]; then
      head -c 32 /dev/urandom | base64 > "$MASTER_KEY"
    fi
    HOUSEHOLD_CONFIG_MASTER_KEY="$(cat "$MASTER_KEY")"
    "${DC[@]}" up -d --no-deps "${SERVICES[@]}" smoke-probe smoke-keys
    # Lemonade embeds for memory as well as hearing and speaking for voice, so without it every
    # memory a turn offers is dropped (lemonade:13305 does not resolve) and recall finds nothing. On
    # the CPU unless STT_BACKEND says otherwise, as voice starts it; its container name is the dev
    # stack's too, so that one must not be running. Waited for below, before the household migration.
    STT_BACKEND="${STT_BACKEND:-cpu}" "${DC[@]}" up -d --no-deps lemonade

    mkdir -p "$KIT_STATE"/{redis,vault,media,sandbox,machine}
    # The outpost binary, published out of this checkout as scripts/run-outpost.sh publishes it, for
    # the kit's machine to run.
    dotnet publish McpServerOutpost/McpServerOutpost.csproj -c Release -o DockerCompose/volumes/smoke/outpost --nologo
    credentials
    # Waited for, not required: a first start pulls several gigabytes of models, and a stack whose
    # memory writes fail for its first minutes is better than no stack.
    retry curl -sf -o /dev/null http://127.0.0.1:13305/api/v1/health \
      || echo "warning: Lemonade is not answering yet; memory is dropped until it does" >&2
    migrate_home
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
  voice)
    # Lemonade hears and speaks for voice (its back-call) and for this check. Its container name is
    # the dev stack's too, so that one must not be running; its caches are the dev stack's volumes.
    STT_BACKEND="${STT_BACKEND:-cpu}" "${DC[@]}" up -d --no-deps lemonade
    retry curl -sf -o /dev/null http://127.0.0.1:13305/api/v1/health
    credentials voice
    SATELLITE="$KIT_STATE/satellite"
    mkdir -p "$SATELLITE" && rm -f "$SATELLITE"/*.wav
    python3 TenantKit/smoke/voice.py say "¿Cuánto es siete por ocho?" "$SATELLITE/q1.wav"
    python3 TenantKit/smoke/voice.py say "Pon un temporizador de un minuto." "$SATELLITE/q2.wav"
    "${KIT[@]}" --profile voice build mcp-channel-voice
    # A voice server left from an earlier run would dial the new satellite at once and then be replaced
    # under it, taking the satellite's one hub connection with it: it goes before the satellite starts.
    "${KIT[@]}" --profile voice rm -sf mcp-channel-voice > /dev/null
    # The satellite first: the hub dials it as it starts.
    "${KIT[@]}" --profile voice up -d --no-deps --force-recreate test-satellite
    "${KIT[@]}" --profile voice up -d --no-deps mcp-channel-voice
    satellite="$(docker ps -q --filter label=com.docker.compose.project=tenant-kit-smoke \
      --filter label=com.docker.compose.service=test-satellite)"
    docker wait "$satellite" > /dev/null
    docker logs "$satellite"
    python3 TenantKit/smoke/voice.py check "$SATELLITE"
    ;;
  household-voice)
    household="${2:-}"
    mode="${3:-answered}"
    [[ -n "${SMOKE_HOUSEHOLD_VOICE_CREDENTIAL:-}" ]] || { echo "SMOKE_HOUSEHOLD_VOICE_CREDENTIAL is not set" >&2; exit 2; }
    [[ "$household" =~ ^[0-9a-f]{32}$ && "$mode" =~ ^(answered|refused)$ ]] \
      || { echo "usage: $0 household-voice <household id> [refused]" >&2; exit 2; }
    STT_BACKEND="${STT_BACKEND:-cpu}" "${DC[@]}" up -d --no-deps lemonade
    retry curl -sf -o /dev/null http://127.0.0.1:13305/api/v1/health
    SATELLITE="$KIT_STATE/household-satellite"
    mkdir -p "$SATELLITE" "$KIT_STATE/household-voices" "$KIT_STATE/household-tse-audit"
    rm -f "$SATELLITE"/*.wav
    python3 TenantKit/smoke/voice.py say "¿Cuánto es siete por ocho?" "$SATELLITE/q1.wav"
    if [[ "$mode" == answered ]]; then
      python3 TenantKit/smoke/voice.py say "Pon un temporizador de un minuto." "$SATELLITE/q2.wav"
    else
      # Nothing set going to wait for.
      export SATELLITE_LINGER=5
    fi
    "${KIT[@]}" --profile voice build mcp-channel-voice
    # The voice server first, and the satellite once its tunnel is open: a question spoken before the
    # household's voice is followed is minted no topic and heard by nobody. The hub dials the
    # satellite again every few seconds until it is there.
    "${KIT[@]}" --profile household-voice up -d --no-deps --force-recreate mcp-channel-voice-household
    retry voice_open "$household"
    "${KIT[@]}" --profile household-voice up -d --no-deps --force-recreate test-satellite-household
    satellite="$(docker ps -q --filter label=com.docker.compose.project=tenant-kit-smoke \
      --filter label=com.docker.compose.service=test-satellite-household)"
    docker wait "$satellite" > /dev/null
    docker logs "$satellite"
    if [[ "$mode" == answered ]]; then
      python3 TenantKit/smoke/voice.py check "$SATELLITE"
    else
      python3 TenantKit/smoke/voice.py refused "$SATELLITE"
    fi
    ;;
  household-vault)
    [[ -n "${SMOKE_HOUSEHOLD_VAULT_CREDENTIAL:-}" ]] || { echo "SMOKE_HOUSEHOLD_VAULT_CREDENTIAL is not set" >&2; exit 2; }
    mkdir -p "$KIT_STATE/household-vault"
    "${KIT[@]}" --profile household up -d --no-deps --force-recreate mcp-vault-household
    ;;
  household-lemonade)
    [[ -n "${SMOKE_HOUSEHOLD_LEMONADE_CREDENTIAL:-}" ]] || { echo "SMOKE_HOUSEHOLD_LEMONADE_CREDENTIAL is not set" >&2; exit 2; }
    "${KIT[@]}" --profile household-lemonade build lemonade-forwarder
    "${KIT[@]}" --profile household-lemonade up -d --no-deps --force-recreate lemonade-forwarder smoke-lemonade-box
    ;;
  migrate)
    # The household migration against the smoke stack, its people file in
    # DockerCompose/volumes/smoke/household-migration/people.json; the rest of the arguments are its.
    shift
    "${DC[@]}" --profile migrate build household-migration
    "${DC[@]}" --profile migrate run --rm --no-deps household-migration --people /input/people.json "$@"
    ;;
  down)
    "${KIT[@]}" --profile voice --profile household --profile household-voice --profile household-lemonade \
      --profile lemonade down --volumes --remove-orphans
    "${DC[@]}" down --volumes --remove-orphans
    # The containers wrote as their own users; a throwaway container removes what they wrote. Only
    # where there is something: bind-mounting a missing ./volumes makes Docker create it as root,
    # and the next up could then not make its directories.
    if [[ -d DockerCompose/volumes/smoke ]]; then
      docker run --rm -v "$ROOT/DockerCompose/volumes:/v" alpine rm -rf /v/smoke
    fi
    ;;
  *)
    echo "usage: $0 up|test|voice|household-voice|household-vault|household-lemonade|migrate|down" >&2
    exit 2
    ;;
esac
