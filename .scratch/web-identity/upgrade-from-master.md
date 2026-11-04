# Upgrading a deployment from master to web-identity

The order, end to end, as rehearsed on a local stack filled the way prod is used (manual test 11,
2026-11-04: three people, topics in both spaces, schedules, a Home Assistant watch, memories, a
Sugary log with doses and pens, a vault, a finished download, an outpost, installed PWAs). Each step
points at the document that says what it does; this file is only the order and what to check.

## Before the checkout

1. **SexyTime** forwards `X-Ziggurat-Assertion` (`.scratch/service-identity/sexytime-note.md`) —
   deploy it first, or every call it makes to `/api/agents` is refused.
2. **Copy the old `.env` aside**: `cp DockerCompose/.env ~/old.env && chmod 600 ~/old.env`. The
   checkout replaces the tracked file; this copy is the migration's input and the source of every
   value carried.
3. **Draft the people**: `scripts/upgrade/people-draft.sh ~/old.env DockerCompose/volumes/voices redis`
   (master has no identity map: the chat client's users, Telegram usernames, voice folders and
   memory buckets are where people are), place the commented names, add each `telegramId`, save it
   as `DockerCompose/volumes/household-migration/people.json`.
4. Install the `ziggurat-sandbox` AppArmor profile on the host the kit runs on (`DockerCompose/apparmor/`).

## The deploy

5. Check out web-identity. `scripts/upgrade/carry-env.sh ~/old.env DockerCompose/.env --generate`;
   decide everything it lists as still empty (`TELEGRAM_SHARED_BOT_TOKEN`, `LAN_BIND_ADDRESS`, ...).
   `OPENROUTER__APIKEY` must not be among them: the home's turns are paid with it.
6. `.scratch/tenants-config/deploy.md` steps 5–8: build `base-sdk`, `up -d --build` (without
   `--remove-orphans` yet), mint the operator's link (`docker exec accounts dotnet Accounts.dll
   mint-link --operator --name <their "name" in people.json>`: the first one needs the name, and
   the migration adopts that account as the operator), the migration's dry run and run with
   `scripts/upgrade/home-keys.sh ~/old.env | … --home-keys -`, restart the agent, `redelegate`.
   Read the dry run's "Schedules" lines: a schedule from master recorded no writer and is given to
   the person whose topic wrote it; one named "goes to the owner" was found nowhere.
7. `TenantKit/README.md`, "Moving the home onto the tunnel", steps 1–6: credentials,
   `--remove-orphans` and Caddy's reload, `satellites.env`, the volumes, the kit's `.env`
   (`scripts/upgrade/carry-env.sh ~/old.env TenantKit/.env`), the state move. Master never had
   `LOGINS__STATEKEY`, so carrying leaves it empty and the kit's web search remembers no logins:
   make one (`openssl rand -base64 32`) — nothing from master is sealed under an older one.
   Home Assistant's `rest_command.assistant_watch_fired` (`docs/home-assistant-bridges.md`) moves
   with its configuration and still names `mcp-homeassistant`, which is the kit's on its network;
   without it no watch reaches anyone, before the upgrade or after.
8. **`redelegate` again** once the kit's seven servers are listed: the watches are the kit's
   Home Assistant's, and a watch from master is offered as the household's owner.
9. Outposts: the old binary keeps logging that the hub refused its registration; run the new one
   with its `outpost-<name>` credential.
10. Clean up: delete `~/old.env`, empty `HOUSEHOLD_MIGRATION_SECRET` and `up -d accounts`.

## What people see, and what to check

- **Installed apps update themselves**: the chat client's, Sugary's and the dashboard's service
  workers are replaced on the first open after the deploy (their files are served signed out), and
  the next open goes to sign-in. Nobody needs to clear site data. A passkey link opened where the
  chat client was installed shows the old app for a moment and then the enrollment page, once the
  new worker has taken over.
- **Sugary left open across the deploy** asks for an "Access code" (master's own sign-in, which the
  deployment never required) and says any code is wrong: tell its person to close the app and open
  it again, twice — the first open installs the new app, the second signs in. Nothing they logged
  before is lost; what they type into that screen goes nowhere.
- **A half-done deploy loses nothing** (rehearsed 2026-11-04): with the accounts service not up,
  every page and call answers 502 and nothing is written; with master's agent image still running
  against the new servers, every server refuses it (unsigned), a person's message waits and is
  answered once the new agent starts, and a due schedule waits in `schedules-due` and fires then.
- Until the migration has run only the operator can sign in, and nothing fires
  (`.scratch/tenants-config/deploy.md`, "Broken from here").
- After it, as each person: their topics (shared, theirs to make private), their memories (recall
  is similarity-only at 0.40: a vague "what do you know about me" may find nothing), Sugary's log
  for the person named `--sugary`, their avatar, their schedules firing into their own topic, the
  sunset-style watches firing as the owner.
- A download that finished before the upgrade has no routing to move; the state move says 0. One
  still running moves with the state move and, when it finishes, its notice lands in the topic that
  asked for it (rehearsed 2026-11-04).
- **The dashboard's Service Health** keeps the deployment's own `mcp-websearch`, `mcp-channel-voice`,
  `tse-extractor` and `music-assistant` as red tiles for up to seven days (`ServiceHealthRegistry`'s
  retention): they moved into the kit, whose tiles carry the household's name.
- A schedule's fires from before the upgrade each opened a "Scheduled task" topic; the migration
  gives each to the schedule's writer by its prompt, and one whose schedule is gone to the owner.
