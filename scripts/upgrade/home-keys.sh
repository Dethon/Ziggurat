#!/usr/bin/env bash
#
# The home's own keys, as the household migration reads them on stdin (--home-keys -), from the .env
# the deployment ran before the upgrade: jack's and jonas's bot tokens (BOTS__0__BOTTOKEN,
# BOTS__1__BOTTOKEN) and Idealista's pair (IDEALISTA__APIKEY, IDEALISTA__APISECRET or IDEALISTA__SECRET). Needs nothing but
# the shell — no jq — and writes nothing anywhere: pipe it straight into the run.
#
#   scripts/upgrade/home-keys.sh <old .env> | docker compose ... --profile migrate run --rm -T \
#     household-migration ... --home-keys - [--dry-run]
#
# A half left empty in the old file is left out, as the migration allows; with neither it prints {}
# and the migration says no keys were given.

set -euo pipefail
[[ $# -eq 1 && -r $1 ]] || { echo "usage: $0 <old .env>" >&2; exit 2; }

read_value() { N="$2" awk 'toupper(substr($0, 1, index($0, "=") - 1)) == toupper(ENVIRON["N"]) { v = substr($0, index($0, "=") + 1) } END { print v }' "$1"; }
json() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '"%s"' "$s"; }

jack=$(read_value "$1" BOTS__0__BOTTOKEN)
jonas=$(read_value "$1" BOTS__1__BOTTOKEN)
key=$(read_value "$1" IDEALISTA__APIKEY)
# Master's server bound Idealista:ApiSecret; its tracked .env's IDEALISTA__SECRET bound nothing, so a
# deployment whose searches worked spelt it IDEALISTA__APISECRET. Either is taken.
secret=$(read_value "$1" IDEALISTA__APISECRET)
[[ -n $secret ]] || secret=$(read_value "$1" IDEALISTA__SECRET)

parts=()
bots=()
[[ -n $jack ]] && bots+=("\"jack\": { \"token\": $(json "$jack") }")
[[ -n $jonas ]] && bots+=("\"jonas\": { \"token\": $(json "$jonas") }")
((${#bots[@]})) && parts+=("\"telegramBots\": { $(IFS=,; echo "${bots[*]}") }")
[[ -n $key && -n $secret ]] && parts+=("\"idealista\": { \"apiKey\": $(json "$key"), \"secret\": $(json "$secret") }")
printf '{ %s }\n' "$(IFS=,; echo "${parts[*]}")"
