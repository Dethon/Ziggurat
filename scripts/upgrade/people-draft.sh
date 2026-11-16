#!/usr/bin/env bash
#
# A first draft of the household migration's people.json for a deployment that never had an identity
# map — master, where who somebody was lived in three places: the chat client's user list
# (USERS__<n>__ID and __AVATARURL in .env), the Telegram bot's allowed usernames
# (ALLOWEDUSERNAMES__<n>) and the voice profiles' folders (volumes/voices/<name>). Every name
# something was filed under is one of those, or a memory bucket in Redis.
#
#   scripts/upgrade/people-draft.sh <old .env> [voices folder] [redis container] > people.json
#
# One person per chat client user, by that name, with their picture as a path on the chat client's
# host. What it cannot know is left as comments the migration's reader skips, for the operator to put
# in place before the dry run: which person each Telegram username and voice folder is (as
# "telegram" and "voice"), each person's numeric Telegram id ("telegramId", what the bot admits by),
# and every memory bucket under a name no person carries (add it to their "oldNames"). Nothing is
# written but the draft; the dry run then says what is still unplaced.

set -euo pipefail
[[ $# -ge 1 && -r $1 ]] || { echo "usage: $0 <old .env> [voices folder] [redis container]" >&2; exit 2; }
ENV_FILE=$1 VOICES=${2:-} REDIS=${3:-}

values() { sed -n "s/^$1=//p" "$ENV_FILE" | grep -v '^$' || true; }
json() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '"%s"' "$s"; }

mapfile -t users < <(grep -oE '^USERS__[0-9]+__ID=.+' "$ENV_FILE" | sed -E 's/^USERS__([0-9]+)__ID=/\1 /' | sort -n)
mapfile -t telegram < <(grep -E '^ALLOWEDUSERNAMES__[0-9]+=.+' "$ENV_FILE" | cut -d= -f2-)
voices=()
[[ -n $VOICES && -d $VOICES ]] && mapfile -t voices < <(find "$VOICES" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)

names=()
echo '{'
echo '  // Each Telegram username below is somebody'\''s "telegram", with their numeric "telegramId":'
for t in "${telegram[@]}"; do echo "  //   telegram: $t"; done
echo '  // Each voice folder below is somebody'\''s "voice":'
for v in "${voices[@]}"; do echo "  //   voice: $v"; done
if [[ -n $REDIS ]]; then
  echo '  // Memory buckets (name: memories). One no person carries goes in their "oldNames";'
  echo '  // household, scheduler, watch and system are the owner'\''s and need no place.'
  docker exec "$REDIS" sh -c 'redis-cli ${REDISCLI_AUTH:+--no-auth-warning} --scan --pattern "memory:*"' 2>/dev/null \
    | awk -F: 'NF >= 3 { n[$2]++ } END { for (b in n) printf "  //   %s: %d\n", b, n[b] }' | sort
fi
echo '  "people": ['
for i in "${!users[@]}"; do
  read -r n id <<< "${users[$i]}"
  avatar=$(sed -n "s/^USERS__${n}__AVATARURL=//p" "$ENV_FILE" | tail -1)
  [[ -n $avatar && $avatar != /* && $avatar != http* ]] && avatar="/$avatar"
  line="    { \"name\": $(json "$id"), \"webChat\": $(json "$id")"
  [[ -n $avatar ]] && line+=", \"avatarUrl\": $(json "$avatar")"
  line+=' }'
  ((i < ${#users[@]} - 1)) && line+=','
  echo "$line"
done
echo '  ]'
echo '}'
