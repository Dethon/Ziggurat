#!/usr/bin/env bash
#
# Publish the outpost binary and run it against the hub.
#
# The outpost is the one server with no Dockerfile and no compose service, so there is
# nothing for run-local.sh to start: it is a self-contained single-file linux-x64 binary
# somebody copies onto a machine and runs with flags. This publishes it out of the
# checkout and runs it in one step, which is what you want on a machine that has the
# repo. A machine that does not have the repo gets the published file copied to it and
# runs it by hand — no script needed, which is the point of the binary.
#
#   scripts/run-outpost.sh                                  this machine, this directory
#   scripts/run-outpost.sh --jailed                          confined to it
#   scripts/run-outpost.sh --name laptop --dir ~/project --jailed --exec
#   scripts/run-outpost.sh --hub http://192.168.1.43:5000 --port 8100
#
# Every flag the binary takes is passed straight through. The script only fills in
# --name, --dir and --hub when you did not type them, so anything you type wins.
#
# Defaults: --name is this machine's hostname, --dir is the directory you ran this from,
# --hub is $DEFAULT_HUB below. Jailing and exec are off unless asked for, as they are in
# the binary — this script does not make either easier to switch on by accident.
#
# SHAREDSECRET is the one value that is not a flag, because a command line is visible to
# every process on the machine. It is taken from the environment, or from
# OUTPOSTS__SHAREDSECRET in DockerCompose/.env when the environment does not have it. An
# empty secret refuses everything at both ends, so an unset one stops the script here
# rather than starting an outpost the hub will not accept and nothing can dial.
#
# Ctrl-C reaches the binary directly (it is exec'd, not backgrounded), so stopping it
# deregisters the machine at once instead of leaving the mount to lapse 90 seconds later.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INVOKED_FROM="$PWD"
PROJECT="$ROOT/McpServerOutpost"
OUT="$PROJECT/bin/outpost"
BIN="$OUT/McpServerOutpost"
ENV_FILE="$ROOT/DockerCompose/.env"
CONFIG="${CONFIG:-Release}"
DEFAULT_HUB="${DEFAULT_HUB:-http://192.168.1.45:5000}"

usage() {
  # The header comment above, to its end: one description, not two that can disagree.
  awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^#[[:space:]]?/, ""); print }' "${BASH_SOURCE[0]}"
  echo "Binary flags: --name --dir --jailed --exec --hub --advertise --port --ext"
}

# Present, in either the `--flag value` or the `--flag=value` form the configuration
# provider accepts. Used only to decide whether a default is needed.
typed() {
  local want=$1 arg
  for arg in "${ARGS[@]:-}"; do
    [[ $arg == "$want" || $arg == "$want="* ]] && return 0
  done
  return 1
}

# One key out of the env file, read rather than sourced: the file is the compose stack's
# and holds every secret the deployment has, none of which this needs.
secret_from_env_file() {
  [[ -f $ENV_FILE ]] || return 0
  local value
  value="$(grep -m1 -E '^[[:space:]]*OUTPOSTS__SHAREDSECRET=' "$ENV_FILE" || true)"
  value="${value#*=}"
  value="${value%\"}"
  value="${value#\"}"
  printf '%s' "${value//[[:space:]]/}"
}

ARGS=("$@")

for arg in "${ARGS[@]:-}"; do
  if [[ $arg == "--help" || $arg == "-h" ]]; then
    usage
    exit 0
  fi
done

command -v dotnet >/dev/null 2>&1 || {
  echo "dotnet is not on PATH — the binary is published from this checkout, not downloaded" >&2
  exit 1
}

secret="${SHAREDSECRET:-}"
[[ -n $secret ]] || secret="$(secret_from_env_file)"
if [[ -z $secret ]]; then
  echo "No shared secret. An outpost without one registers with nobody and answers nobody:" >&2
  echo "  the hub refuses an unset secret, and so does this binary's own /mcp." >&2
  echo "Set OUTPOSTS__SHAREDSECRET in $ENV_FILE (the same value the hub reads), or export" >&2
  echo "SHAREDSECRET for this run. It is deliberately not a flag." >&2
  exit 1
fi

# Every run, because the whole point of publishing from the checkout is that the binary
# is the code you have. It is incremental, so an unchanged tree costs a few seconds.
echo "publishing $CONFIG → $OUT"
dotnet publish "$PROJECT/McpServerOutpost.csproj" -c "$CONFIG" -o "$OUT" --nologo

[[ -x $BIN ]] || {
  echo "publish produced no runnable binary at $BIN" >&2
  exit 1
}

flags=()
typed --name || flags+=(--name "$(hostname -s 2>/dev/null || uname -n)")
# Absolute, so the workspace the mount declares is the same directory whatever the
# binary's own working directory turns out to be.
typed --dir || flags+=(--dir "$INVOKED_FROM")
typed --hub || flags+=(--hub "$DEFAULT_HUB")
if [[ ${#ARGS[@]} -gt 0 ]]; then
  flags+=("${ARGS[@]}")
fi

echo "running: McpServerOutpost ${flags[*]}"

# exec, so Ctrl-C and any supervisor's TERM land on the binary itself: a clean stop is
# what sends the deregistration.
exec env SHAREDSECRET="$secret" "$BIN" "${flags[@]}"
