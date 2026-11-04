# Deploying Tenants 4b (household configuration)

4b reaches prod with the rest of `web-identity` (accounts, the tunnel, service identity), so its steps
sit inside the accounts deploy (`.scratch/accounts/spec.md`, Further Notes): **the household migration
is one run of one command**, and 4b's part of it is the home's configuration and the shared stores'
re-keying beside the people's accounts. What each step does is `.claude/rules/household-migration.md`;
the commands are also in the launch-stack skill ("Migrating into the home household").

## Before deploying

1. **Copy out what the new compose file stops reading.** The old `.env` is the migration's input, and
   the checkout of the new code replaces the tracked file: copy it aside first
   (`cp DockerCompose/.env ~/old.env`, `chmod 600`) and fill the new one from it with
   `scripts/upgrade/carry-env.sh ~/old.env DockerCompose/.env --generate` — it carries every value both
   files name (`OPENROUTER__APIKEY` among them: a home with none is refused every turn), makes the
   secrets no older deployment had, and lists what is still empty for you to decide. From the copy:
   - the people into `DockerCompose/volumes/household-migration/people.json` (accounts step 1). A
     deployment that ran web-identity's identity map has them in `mcp-websearch`'s environment; one
     from master never had a map, and `scripts/upgrade/people-draft.sh ~/old.env DockerCompose/volumes/voices redis`
     drafts the file from the chat client's users (and their pictures), with the Telegram usernames,
     the voice folders and the memory buckets left as comments for you to place;
   - `BOTS__0__BOTTOKEN` (jack's bot), `BOTS__1__BOTTOKEN` (jonas's) and `IDEALISTA__APIKEY`,
     `IDEALISTA__SECRET`, which step 6 pipes into the run from the copy (`scripts/upgrade/home-keys.sh`).
     Nothing reads them after the deploy (compose no longer passes them).
2. **The master key**, `.env`'s `HOUSEHOLD_CONFIG_MASTER_KEY`: `openssl rand -base64 32`. It wraps every
   household's data key, so it is the one key to all of their secrets: keep a copy off the host.
   Losing it loses every household's keys (they are entered again); rotating it is
   `rewrap-household-keys` (`.env`'s comment). With none, every keys save — the migration's too — is
   refused (503), and the home is answered no bots and no Idealista key.
3. **The shared bot**, `.env`'s `TELEGRAM_SHARED_BOT_TOKEN`: the one bot every household's people talk
   to and link their Telegram through. Best a new bot (BotFather): the home keeps jack and jonas as its
   own. If jonas's token is reused as the shared one, the home's jonas entry is left out
   (`BotRegistry`: a household bot whose token is the shared one is not polled twice) and the shared bot
   answers the home's people with its default agent, jonas — the same thing by another road. Set
   `TELEGRAM__BOTUSERNAME` in compose's `accounts` service to the shared bot's username without the
   `@` (accounts step 2): link codes and invites are its `t.me` deep links.
4. `HOUSEHOLD_MIGRATION_SECRET` in `.env` (`openssl rand -hex 32`, accounts step 4).

## Deploy

5. `docker compose build base-sdk`, then `up -d --build` the stack (accounts step 2: the accounts
   service's per-deployment values and Caddy's session-check routes, `mcp-sugary` started once).
   The accounts service starts with the master key and admits the migration.

### Broken from here until the migration has run

Everything the home had is still there, under the shapes from before households, which nothing reads
any more. So run the migration straight after the deploy. Meanwhile:

- **Nobody but the operator is an account** (accounts): a Telegram sender is refused until their id is
  linked, a person's memories are under their old names.
- **The home runs the two templates**, barebones `jonas` and `sugary` (`HouseholdAgents.Starting`):
  jack and nabu do not exist, so voice turns are refused, and the chat client's picker lists two
  agents.
- **jack's and jonas's bots are silent**: their tokens are nowhere the Telegram server reads. Only the
  shared bot answers, with the household's default agent.
- **Idealista searches nothing**: the deployment holds no key, the home has saved none, and a household
  that saved no features has Idealista off.
- **The chat client shows one space** (Main, the default of a household that saved none) and **no topic
  from before**. Phones get no push.
- **No watch reaches anyone**: until the home's Home Assistant server is up in the tenant kit, a watch
  that fires posts to the deployment's old `mcp-homeassistant` (left running until
  `--remove-orphans`), which no agent dials any more. That fire is lost, not deferred.
- **No schedule fires**: the dispatcher reads only the household shapes. A schedule due meanwhile
  fires on the dispatcher's first tick after the migration moves it; a cron then keeps its times.
- **The dashboard** shows only what was counted since the deploy; the days before are merged in.

## The migration

6. Dry run, with the home's keys piped in from the old `.env` (never on a command line, never in the
   volume), until it lists every bucket and nothing is "left as it was":

   ```bash
   scripts/upgrade/home-keys.sh ~/old.env | docker compose -f DockerCompose/docker-compose.yml -p jackbot \
     --profile migrate run --rm -T household-migration --people /input/people.json --operator <name> \
     --owner <name> --sugary <name> --home-keys - --dry-run
   ```

   `-T` is what lets compose pass the pipe through; the script needs nothing but the shell (no `jq`)
   and writes nothing. Either half of the keys may be left out; the OpenRouter key may not — the
   home's turns are paid with the deployment's.
   The dry run says what each step would move — the people, the home's settings (the deployment's
   zone), agents (jack, jonas, nabu, sugary), features (every one, Idealista too), keys (the two bots,
   Idealista's pair), spaces (Main and X), memories, logins, phones and spaces' phones, schedules,
   topics and the old search index, voices, the Sugary log, the roll-ups — and how many changes the
   run will make. It checks every keys document against the accounts service, so a missing master key
   shows here (503).
7. The same without `--dry-run`. Keep the output: the passkey links it prints are shown this once.
   A rerun changes nothing and says so; a run that stopped is finished by running it again.

   The keys go in through the seed route, which gives a keys section the household already saved only
   the keys it lacks: if the operator saved Idealista's keys by hand first (`set-household-config
   --section keys`), they are kept and the bots are added beside them ("kept as they are"). Nothing an
   owner saved in `settings`, `agents`, `features` or `spaces` is put over either.

## After the migration

8. **What re-reads by itself and what restarts.** The seed's saves are announced
   (`accounts:household-config:saved`), so every reader drops what it kept of the home: the Telegram
   server starts jack's and jonas's bots within its five-second refresh; the agent's catalogue for its
   channels re-reads on its interval; Idealista asks again within its one-minute cache. Restart:
   - `docker compose restart agent`: a conversation keeps the agent it was first built with
     (`ConversationGroup`), so one that began between the deploy and the run would go on as a
     template; a restart builds every conversation from the seeded agents. Then
     `docker exec -it agent dotnet Agent.dll redelegate` (service identity) — after the migration, which
     moved the schedules into the home and stamped their writers, so it offers each as its writer.
     A schedule from master recorded no writer: the migration finds them in the topic that wrote it,
     and names each one it could not (given to the owner) with the start of its prompt. Run
     `redelegate` **again once the tenant kit is up** (`TenantKit/README.md`, "Moving the home onto the
     tunnel"): Home Assistant's watches are kept by the kit's server, which this first pass lists as
     not asked, and a watch from master acts for nobody until that pass offers it as the household's
     owner.
   - `mcp-channel-voice` and `tse-extractor`, if they still run beside the deployment: speaker profiles
     are read at start and their folders are named by user id now (accounts step 7). If the home is
     moving into the tenant kit, the move carries the renamed folders instead (`TenantKit/README.md`,
     "Moving the home onto the tunnel", which runs after this).
   - The chat client: reload the page (it asks the hub for the spaces once connected).
9. **Clean `.env`**: delete `BOTS__0__BOTTOKEN`, `BOTS__1__BOTTOKEN`, `IDEALISTA__APIKEY` and
   `IDEALISTA__SECRET` (the home's keys section holds them now, sealed), then empty
   `HOUSEHOLD_MIGRATION_SECRET` and `docker compose up -d accounts` (accounts step 9). Keep
   `HOUSEHOLD_CONFIG_MASTER_KEY`.
10. **Check**, signed in: the chat client lists Main and X with their topics, jack and jonas answer on
    their own bots, nabu answers a satellite, a property search runs, the dashboard shows the days
    before the deploy. `docker exec accounts dotnet Accounts.dll get-household-config --household home
    --section keys` shows the bots and Idealista as `{"sealed": true}`.
