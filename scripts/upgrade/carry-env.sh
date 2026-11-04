#!/usr/bin/env bash
#
# Carries a deployment's values from the .env it ran before the upgrade into the new tracked one.
#
#   scripts/upgrade/carry-env.sh <old .env> <new .env> [--generate]
#
# Every line of the new file left empty takes the old file's value of the same name, where the old
# file has one; nothing already filled in the new file is touched. With --generate, the new secrets
# no older deployment had are made here: REDIS_PASSWORD and every MCP_SECRET_* (openssl rand -hex 32,
# each its own), HOUSEHOLD_CONFIG_MASTER_KEY (openssl rand -base64 32 — keep a copy off the host: it
# is the one key to every household's secrets) and HOUSEHOLD_MIGRATION_SECRET (for the one migration
# run, emptied afterwards). It then lists what is still empty, so the operator decides each.
#
# The values are never printed: only names. Works on the deployment's .env and on the tenant kit's
# (TenantKit/.env, with the deployment's old .env as the source of the keys that moved into the kit).

set -euo pipefail
[[ $# -ge 2 ]] || { echo "usage: $0 <old .env> <new .env> [--generate]" >&2; exit 2; }
OLD=$1 NEW=$2 GENERATE=${3:-}
[[ -r $OLD && -w $NEW ]] || { echo "$OLD must be readable and $NEW writable" >&2; exit 2; }

value_in() { sed -n "s/^$1=//p" "$OLD" | tail -1; }

set_value() {
  local name=$1 value=$2 tmp
  tmp=$(mktemp)
  # Through the environment, not -v, which would read a backslash in a value as an escape.
  N="$name" V="$value" awk 'BEGIN { FS = "=" } $1 == ENVIRON["N"] && !done { print ENVIRON["N"] "=" ENVIRON["V"]; done = 1; next } { print }' "$NEW" > "$tmp"
  cat "$tmp" > "$NEW"
  rm -f "$tmp"
}

generated_for() {
  case $1 in
    REDIS_PASSWORD|MCP_SECRET_*|HOUSEHOLD_MIGRATION_SECRET) openssl rand -hex 32 ;;
    HOUSEHOLD_CONFIG_MASTER_KEY) openssl rand -base64 32 ;;
    *) return 1 ;;
  esac
}

carried=() generated=() empty=()
while IFS= read -r name; do
  if old=$(value_in "$name") && [[ -n $old ]]; then
    set_value "$name" "$old"; carried+=("$name")
  elif [[ $GENERATE == --generate ]] && fresh=$(generated_for "$name"); then
    set_value "$name" "$fresh"; generated+=("$name")
  else
    empty+=("$name")
  fi
done < <(grep -E '^[A-Za-z_][A-Za-z0-9_]*=$' "$NEW" | cut -d= -f1)

echo "carried from $OLD: ${carried[*]:-nothing}"
[[ $GENERATE == --generate ]] && echo "generated: ${generated[*]:-nothing}"
echo "still empty: ${empty[*]:-nothing}"
