---
paths:
  - "HouseholdMigration/**"
  - "Tests/Integration/Migration/**"
  - "Accounts/People/HouseholdImporter.cs"
  - "Accounts/Internal/HouseholdImportApi.cs"
---

# The household migration

`HouseholdMigration/` is the one-shot command that carries the retired identity map's people, and everything filed under their names, into the home household (`.scratch/accounts/spec.md`, "Migration"; the deploy steps are its Further Notes). It is a console project, not a server: the compose service `household-migration` sits under the `migrate` profile so no `up` starts it, and the operator runs it with `docker compose --profile migrate run --rm household-migration --people /input/people.json --operator <name> --owner <name> --sugary <name> [--dry-run]`. Every `<name>` is a person's `name` in the file, the identity map's Name.

## The people's file

The identity map lived only in prod's compose environment (`IDENTITY__PEOPLE__<n>__{NAME,TELEGRAM,WEBCHAT,VOICE}`, retired by ticket 09), so the people are input, not code: `./volumes/household-migration/people.json`, `{ "people": [ { name, displayName?, telegram?, telegramId?, webChat?, voice?, oldNames?, role?, avatarUrl? } ] }`. `telegramId` is the numeric id the bot links people by, which the map never held. `avatarUrl` is the picture the retired chat client's `Users` list showed beside them (WebChat's old `appsettings.json`), written as a path on the chat client's host (`/avatars/kakera.png`, not the old relative `avatars/kakera.png`, which the account page's rule refuses) or an http(s) address. `oldNames` is every other name something of theirs may be filed under — a Telegram first name, a satellite identity, a name spelled two ways — because **Redis keys are case-sensitive**, so `Fran` and `fran` are two buckets; principals are matched without case, as the channels that issued them matched. `PeopleFile.Problems` refuses, before anything is touched, a name listed twice, a name two people share, a glob character, `:` or a path separator in a name (it would be read as a Redis pattern or leave `/voices`), a person's name equal to one of the owner's buckets, and a run naming somebody the file does not list.

## Idempotence

Every step moves only what is still under an old name, so a second run finds nothing and prints "Nothing changed"; a run that stopped part way is finished by running it again. `--dry-run` writes nothing anywhere: the accounts service runs the import inside a transaction it rolls back, and every step counts instead of writing. The exit code is 0, 2 for a refused run (usage, the file, a missing credential or a malformed `LOGINS__STATEKEY` — nothing touched), and 1 when the accounts service refused the import or a step left something where it was (said under "left as it was").

## Accounts

The people become accounts first, through `POST /accounts/internal/household-import` (`HouseholdImportApi` over `HouseholdImporter`), because every other step files things under the user ids it answers. All or nothing, one transaction:

- **Found again by a linked identity on the retired `identity-map` channel**, the person's map Name (`HouseholdImporter.Channel`). No resolution answers that channel, so it is inert everywhere but here; no schema changed.
- **Adopted before created**, in order: the one an earlier run made; for the person named operator, the operator who already ran `mint-link --operator` (the likely case: it is how anyone signs in before the migration); anybody already holding the Telegram id listed for them (they redeemed an invite or a link code first). An adopted account keeps its display name, and keeps a picture its person chose on the account page (a note); one with none is given the file's `avatarUrl`. The name and picture are held to the account page's rule (`Profile.Read`), and a bad one refuses the whole import (400). A person who would be an account another key already names is a conflict.
- **Roles only go up.** The operator is made an owner and the operator, the owner an owner, the rest what the file says (member by default) when created; nobody is demoted, so a role changed on the members page since is not undone. A second operator is a conflict.
- **Telegram** is linked when the person holds none; a Telegram another person holds refuses the whole import (409, naming them), and a person who linked a different one since keeps theirs (a note, not a change). A voice profile needs no row: the folder rename is the link.
- **A passkey link is minted only for a person this run created**, after the commit, and printed once. Anybody else is shown `mint-link --user <id>` while they hold no passkey, so a second run mints nothing.
- **Only the migration's own credential is admitted** (`CALLERS__household-migration`, 403 for any other caller the internal API admits), because it can make the operator and mint their link. Compose feeds it `HOUSEHOLD_MIGRATION_SECRET`, a placeholder left empty — nobody — except for the run.

## The stores

Each step goes through the store's own code where one exists, and spells a key shape itself only to read or delete under an old name.

- **Memories** (`MemoryBuckets`, through `RedisStackMemoryStore`): every bucket under a person's names merged into `memory:{userId}`, ids, embeddings and timestamps as they were, re-indexed by the store's write; a memory whose trimmed text the account already holds is dropped. The latest profile of the old names is kept unless the account has one, and the old profiles go. The buckets that were nobody's — `household` (the voice guest), `scheduler` and `watch` (fires), `system` (download alerts) — go to the owner, profiles left. Buckets under names nobody is listed with are named in the output, not moved: only the operator knows whose they are. The store is given `MemoryDimension` (1024, the agent's) only so a run that had to create the index would not make it at a wrong width.
- **Remembered logins** (`RememberedLogins`, through `RedisPersonStateStore`): the record under the map Name opened with that name as associated data and sealed under the user id with the same `LOGINS__STATEKEY`. An account that already remembers logins keeps them and the old record is left (merging two browsers' cookies is a guess); a record no key opens is left and reported.
- **Push** (`PushSubscriptions`, through `RedisPushSubscriptionStore`): each `push:subs:{old}` entry saved again under the account by the store's own `SaveAsync`, its keys and space as they were (the save points `push:ep:{endpoint}` at the account; the space set already holds the endpoint), and the old entry cleared. An endpoint somebody else subscribed since just loses its stale entry.
- **Schedules** (`ScheduleCreators`): one whose `CreatedBy` carries no account is re-stamped in place, its own TTL kept (`keepTtl`, `When.Exists`, so a one-shot that fired meanwhile is not brought back). A principal naming a person keeps its channel and gains their account, its user id becoming the account's on `signalr` and `voice` (where a turn's user id is the account's now) and staying the name elsewhere (Telegram's approval router matches it). One naming nobody, or none at all, runs as the owner, as `Principal("signalr", owner, owner's account)`.
- **Topics** (`TopicOwners`): only a topic with **neither owner nor household** is from before — a Telegram group's review copy is shared and the household's with no owner on purpose, and is left. Each is marked shared (the spec) and given an owner, since only an owner may stop sharing one: whoever its first user message's principal or sender names, else the household's owner. `OwnerName` is set only for a starter, so a topic the owner was handed does not say they began it.
- **Voice profiles** (`VoiceProfiles`): `/voices/<voice>` renamed to `/voices/<userId>`, takes and cached prototypes together; a folder already under the id is left and reported. The voice channel and the TSE extractor read the folders at start, so both restart after the run.
- **Sugary** (`SugaryLog`, through the server's own `SugaryDbContext` with its filters set aside): the database migrated as the server would, then every `legacy` row of the personal tables given to the person named `--sugary` and every `legacy` food and recipe to the household. No file at `SugaryDatabasePath` is no log to give.

## Configuration

Environment variables over `MigrationSettings`' defaults (`/sugary/sugary.db`, `/voices`, `http://accounts:8080`), and **no `appsettings.json`**: the Sugary server's own comes with its project reference into the same output, and a second one is a publish conflict. The compose service mounts `./volumes/sugary` and `./volumes/voices` read-write as `PUID:PGID` (their services' user) and `./volumes/household-migration` read-only, and reads `REDISCONNECTIONSTRING`, `LOGINS__STATEKEY` and `ACCOUNTS__CREDENTIAL`; `DeploymentConformanceTests` holds each.

## Tests

`HouseholdMigrationCommandTests` runs the container's command (`HouseholdMigrationCommand.RunAsync`) against the accounts service in-process (`AccountsServer`, admitting the migration's credential), a Sugary database and a voices folder of the test's own (`MigrationRig`), and a Redis Stack of the class's own: `HouseholdMigrationRedisFixture` asks for a pool by a name nobody else uses, because the memory index lives on database 0, which the search pools share between classes, and the run moves the `household` bucket and scans every topic and schedule — on a shared database it would take a sibling's rows mid-run. Old names carry a per-test tag. "Changes nothing" is asserted on a snapshot of every Redis key's dump, the accounts store's counts, the voices folder and the log's owners. `HouseholdImportTests` drives the import endpoint raw.
