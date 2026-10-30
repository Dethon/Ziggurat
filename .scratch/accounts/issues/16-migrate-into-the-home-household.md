# 16: Migrate into the home household

**What to build:** Nobody starts over. One idempotent command, given the operator, the owner and the person whose Sugary log it is, creates one account per identity-map person with Telegram id, voice profile and chat-client name linked, and prints each a one-time passkey link. It merges each person's memories from every channel name into their account and the household bucket into the owner's, re-seals remembered logins under the user id, renames voice profiles, re-keys push subscriptions, re-stamps schedules' creators (to the owner where the old principal names nobody), gives today's Sugary rows to the named person, and marks every existing topic shared. Spec: `../spec.md` ("Migration").

**Blocked by:** 09, 10, 11, 14

**Status:** done

- [x] Against a seeded Redis and Sugary database: accounts created and linked, memories merged, logins re-sealed and readable, schedules re-stamped, Sugary rows owned, topics shared
- [x] A second run changes nothing
- [x] The deploy steps are in the spec's Further Notes and the stack-launch instructions

What was built (`.claude/rules/household-migration.md` has the detail):

- `HouseholdMigration/`, a console project and the compose service `household-migration` under the `migrate` profile: `docker compose --profile migrate run --rm household-migration --people /input/people.json --operator <name> --owner <name> --sugary <name> [--dry-run]`. The people are input (`./volumes/household-migration/people.json`), since prod's identity map lives only in prod's compose environment; each carries the map's four values plus the numeric `telegramId` and any `oldNames`.
- The accounts service gains `POST /accounts/internal/household-import`, admitted only by `CALLERS__household-migration` (`HOUSEHOLD_MIGRATION_SECRET`, empty but for the run). A person is found again by a `LinkedIdentity` on the `identity-map` channel; the operator who already ran `mint-link --operator`, and anybody already linked to the listed Telegram, is adopted rather than made twice. Links are minted only for people the run created.
- Beyond the spec: the `scheduler`, `watch` and `system` memory buckets go to the owner with `household` (10's question); a topic is owned by whoever its first message names, the owner otherwise, and shared either way, so people can make their own private (11's note); a topic with a household but no owner — a Telegram group's copy (12) — is left alone. A `--dry-run` writes nothing anywhere.

Notes for later work:

- After the run, `mcp-channel-voice` and `tse-extractor` must restart: speaker profiles are read at start.
- Old keys the run could not place are named, not guessed: memory buckets under names nobody is listed with, a logins record when the account already remembers logins, a voice folder when one already exists under the user id.
- `SugaryServer.GiveUnassignedRowsToAsync` remains the suite's stand-in; the real step is `HouseholdMigration/Steps/SugaryLog.cs`.
