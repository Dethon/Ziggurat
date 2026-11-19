---
paths:
  - "HouseholdMigration/**"
  - "Tests/Integration/Migration/**"
  - "Accounts/People/HouseholdImporter.cs"
  - "Accounts/Internal/HouseholdImportApi.cs"
---

# The household migration

`HouseholdMigration/` is the one-shot command that carries the retired identity map's people, and everything filed under their names, into the home household (`.scratch/accounts/spec.md`, "Migration"; the deploy steps are its Further Notes). It is a console project, not a server: the compose service `household-migration` sits under the `migrate` profile so no `up` starts it, and the operator runs it with `docker compose --profile migrate run --rm household-migration --people /input/people.json --operator <name> --owner <name> --sugary <name> [--dry-run]` (`HouseholdMigrationCommand`). Every `<name>` is a person's `name` in the file.

## The people's file

The people are input, not code: `./volumes/household-migration/people.json`, `{ "people": [ { name, displayName?, telegram?, telegramId?, webChat?, voice?, oldNames?, role?, avatarUrl? } ] }` (`PeopleFile`), which `scripts/upgrade/people-draft.sh` drafts for a deployment from master — keep the shape and the script in step. `telegramId` is the numeric id the bot links people by (`telegram` the username). `avatarUrl` is a path on the chat client's host (`/avatars/kakera.png`, never the old relative form, which the account page's rule refuses) or an http(s) address. `oldNames` lists every other spelling, because **Redis keys are case-sensitive** (`Fran` and `fran` are two buckets); principals are matched without case (`Household`). `PeopleFile.Problems` refuses a file that would make the run guess before anything is touched — among it a glob character, `:` or a path separator in a name, which would be read as a Redis pattern or leave `/voices`.

## Idempotence

Every step moves only what is still under an old name, so a second run finds nothing and prints "Nothing changed", and a run that stopped part way is finished by running it again. `--dry-run` writes nothing anywhere — the accounts service runs the import in a transaction it rolls back, and every step counts instead of writing. Exit codes: 0; 2 for a refused run (usage, the file, a missing credential or a malformed `LOGINS__STATEKEY` — nothing touched); 1 when the accounts service refused the import or a step left something where it was ("left as it was").

## Accounts

The people become accounts first, through `POST /accounts/internal/household-import` (`HouseholdImportApi` over `HouseholdImporter`), because every other step files things under the user ids it answers. All or nothing, one transaction:

- **Found again by a linked identity on the retired `identity-map` channel** (`HouseholdImporter.Channel`), which no resolution answers, so it is inert everywhere but here.
- **Adopted before created**, in order (the earlier run's, the operator who ran `mint-link --operator`, whoever holds the listed Telegram id); an adopted account keeps its name and a picture its person chose. Name and picture are held to `Profile.Read`; a bad one refuses the whole import (400).
- **Roles only go up.** On creation the `--operator` person is made an owner and the operator, the `--owner` person an owner, the rest what the file says (member by default); nobody is demoted, so a role changed on the members page since is not undone. A second operator is a conflict.
- **Telegram** is linked when the person holds none; one another person holds refuses the whole import (409). A voice profile needs no row: the folder rename (`VoiceProfiles`) is the link.
- **A passkey link is minted only for a person this run created**, after the commit, printed once; anybody else is shown `mint-link --user <id>` while they hold no passkey, so a rerun mints nothing.
- **Only the migration's own credential is admitted** (`CALLERS__household-migration`, 403 for any other internal caller), because it can make the operator and mint their link. Compose feeds it `HOUSEHOLD_MIGRATION_SECRET`, left empty — nobody — except for the run.

## The stores

Each step goes through the store's own code where one exists, and spells a key shape itself only to read or delete under an old name. The steps are `HouseholdMigration/Steps/`, in the order `HouseholdMigrationCommand` lists them; each file's header says what it moves and what it leaves.

- **Memories** (`MemoryBuckets`): every bucket under a person's names merged into `memory:{userId}` through `RedisStackMemoryStore`; the nobody buckets (`household`, `scheduler`, `watch`, `system`) go to the owner; buckets under unlisted names are reported, not moved.
- **Remembered logins** (`RememberedLogins`): re-sealed from the map Name to the user id under the same `LOGINS__STATEKEY`.
- **Push** (`PushSubscriptions`): re-saved under the account by `RedisPushSubscriptionStore.SaveAsync`.
- **Schedules** (`ScheduleCreators`, `ScheduleWrites`): stamped in place under the old shape with their own TTL (`keepTtl`, `When.Exists`, so a one-shot that fired meanwhile is not brought back). A principal's user id becomes the account's on `signalr` and `voice` and stays the name elsewhere (Telegram's approval router matches it). One recording no principal at all is looked for in the conversations (`ScheduleWrites`: the schedule mount's `created`/`edited` answer in a topic, latest write winning); one naming nobody, or found nowhere, runs as the owner (`Principal("signalr", owner, owner's account)`), fires into the owner's private topic, and is named in the output.
- **Topics** (`TopicOwners`): only a topic with **neither owner nor household** is from before — a Telegram group's review copy is ownerless on purpose and left. Each is marked shared and given an owner (only an owner may stop sharing one); `OwnerName` only for a starter.
- **The home's configuration** (`HomeConfiguration`, first): settings with the run's `TZ` and no language, agents from `HouseholdMigration/Home/agents.json` (`defaultAgent` jonas; `TestAgentsTests` holds `Tests/TestAgents.json` equal to it) and features from `Home/features.json`, embedded, each through the accounts seed route (`HouseholdConfigSeed`): a section the household already saved is left as it is.
- **The smoke stack runs the migration itself** (`scripts/smoke-stack.sh up`, in the order `.scratch/tenants-config/deploy.md` gives, over schedules and a topic it writes in the old shape first, `AScheduleWrittenBeforePrincipals_RunsAsThePersonWhoseTopicWroteIt`); **a new step that seeds or moves something belongs in the smoke tier's checks too.**
- **Shared stores into the household** (`ScheduleHouseholds`, `PushSpaces`, `TopicHouseholds`, each after the step that fixes the same records' owners): what is still under the shape from before households moves into the home household (a topic into the household it records). The new shapes put an `@` after the store's prefix (`schedule@`, `push:space@`, `topic@`, `topics@`, `topicdoc@`) precisely so these steps' patterns (`schedule:*` via the `schedules` set, `push:space:*`, `topic:*`, `topics:*`, `topicdoc:*`) can never match a household's key — a store that changes its shape keeps that `@` (`.claude/rules/scheduling.md`, `.claude/rules/chat-hub.md`). Each moves with its expiry and ordering (a due schedule into `schedules-due` as `{tenantId}:{id}`, a topic's pointer rewritten to `{tenantId}:{topicId}`, its search document given the `household` field `idx:household-topics` filters on); `idx:topics` is dropped without its documents and `RedisThreadStateStore.EnsureSearchIndexAsync` makes `idx:household-topics`. A key the household already holds is left and reported. Outposts have no store to move (`TunnelledOutposts`, `.claude/rules/tunnel.md`).
- **The home's own keys** (`HomeKeys`, straight after `HomeConfiguration`): jack's and jonas's bot tokens and Idealista's pair, seeded sealed as the home's `keys` section. They are the run's input, never its command line or a file in the volume: `--home-keys -` reads `{ "telegramBots": { "jack": { "token": "..." }, "jonas": { "token": "..." } }, "idealista": { "apiKey": "...", "secret": "..." } }` from stdin (`... run --rm -T household-migration ... --home-keys -`; `scripts/upgrade/home-keys.sh` writes it from the old `.env` with nothing but the shell), or a path. The file is checked before anything is touched — either half or both, each bot one of `Home/agents.json`'s with a token, Idealista's as both of the pair, **no OpenRouter key** (the home's turns are paid with the deployment's) and nothing else — and a problem is exit 2 naming no value. A keys section already saved is given only the keys it lacks (`.claude/rules/household-config.md`). A service with no master key refuses the seed (503).
- **The chat client's spaces** (`HouseholdSpaces`): the list the chat client shipped saved as the home's `spaces` section, naming no default agent; a household that already saved spaces keeps them. The snapshot the rerun and dry-run tests compare counts every household section's document.
- **Voice profiles** (`VoiceProfiles`): `/voices/<voice>` renamed to `/voices/<userId>`; the voice channel and the TSE extractor read the folders at start, so both restart after the run.
- **Metric roll-ups** (`MetricRollups`, `MetricKeys`): old daily keys (`metrics:{family}:{yyyy-MM-dd}`, told apart by having three segments) and `metrics:errors:recent` move under the home's scope, added to `metrics:scopes`. The deploy's day exists in both shapes, so a key is **merged, never renamed over** (a time series by union at `Aggregate.Max`, so a member in both is not rescored; a totals hash by `HINCRBY` per field; old errors pushed behind and trimmed to 100), keeping the home's expiry or else the old one's, and deleted in one transaction conditioned on the old key existing, so a rerun counts no day twice. Nothing under `deployment` or a household's id is touched, and the TTL'd health keys are left to expire.
- **Sugary** (`SugaryLog`, through `SugaryDbContext` with its filters set aside): `legacy` personal rows to `--sugary`'s person, `legacy` foods and recipes to the household (`.claude/rules/sugary.md`).

## Configuration

Environment variables over `MigrationSettings`' defaults, and **no `appsettings.json`**: the Sugary server's own comes with its project reference into the same output, and a second one is a publish conflict. The compose service mounts `./volumes/sugary` and `./volumes/voices` read-write as `PUID:PGID` and `./volumes/household-migration` read-only, and reads `REDISCONNECTIONSTRING`, `LOGINS__STATEKEY` and `ACCOUNTS__CREDENTIAL`; `DeploymentConformanceTests` holds each.

## Tests

The end-to-end cases (`ADryRun_ReportsEveryStepsMoves_AndWritesNothingAnywhere`, `AfterTheRun_ASignedInPersonSeesTheirTopicsSchedulesAndSpaces`, `RunningItAgain_ChangesNothingAndSaysSo`) run over `SeedTheHomeAsync`, where every step has something to move: the dry run must name no step with "nothing to move" and count exactly the run's changes, and the result is read the way the channels read it (stores by household and viewer, `MigrationRig.Reader` on a signed-in assertion) — never by raw key. **A new step must give that seed something to move.**

Old-shape data is seeded as those stores wrote it (`BeforeHouseholds`). `HouseholdMigrationCommandTests` runs `HouseholdMigrationCommand.RunAsync` against the accounts service in-process (`MigrationRig`) and a Redis Stack of the class's own (`HouseholdMigrationRedisFixture`): never a shared search pool, since the run moves the `household` bucket and scans every topic and schedule on database 0. Old names carry a per-test tag. "Changes nothing" is asserted on a snapshot of every Redis key's dump, the accounts store's counts, the voices folder and the log's owners. `HouseholdImportTests` drives the import endpoint raw.

## The home's state move (`move-home-state`)

The same image carries a second verb (`HomeStateMove`, `TenantKit/README.md`, "Moving the home onto the tunnel"): it copies web search's `websearch:person:*` and `websearch:link:*` and the library's `download-routing:*` (and its `download-routing` index) from the deployment's Redis into the kit's, and nothing else. It runs after the household migration, which re-seals the logins it carries. The kit's `state-move` service (profile `move`, `network_mode: host`) gives `HOMESTATE__TO`; `HOMESTATE__FROM` is given on the run's own command line, because its password is the deployment's and the kit's files hold no deployment secret.

- **Each key goes as it is**: `DUMP`/`RESTORE` with its remaining TTL, so a sealed login stays sealed — the kit's `LOGINS__STATEKEY` must be the deployment's value.
- **It copies and never overwrites.** A key the kit holds is the kit's and newer; only the download index is merged. The deployment keeps its copy.
- `--dry-run` counts and writes nothing; a missing address is exit 2, naming it. `HomeStateMoveTests` reads the result back through the owning stores.
