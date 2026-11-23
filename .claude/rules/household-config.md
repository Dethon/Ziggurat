---
paths:
  - "Accounts/Configuration/**"
  - "Accounts/Internal/HouseholdConfiguration.cs"
  - "Accounts/Internal/InternalCallers.cs"
  - "Accounts/Commands/HouseholdConfigCommands.cs"
  - "Domain/DTOs/Household/**"
  - "Domain/Contracts/IHouseholdConfiguration.cs"
  - "Infrastructure/Clients/Accounts/HouseholdConfigurationClient.cs"
  - "Infrastructure/Clients/Accounts/AccountsClientConfig.cs"
  - "Tests/Integration/Accounts/HouseholdConfiguration*"
  - "Mcp.Hosting/CallHousehold.cs"
  - "Domain/Contracts/ICallHousehold.cs"
  - "Tests/Integration/McpServers/CallHouseholdTests.cs"
  - "Accounts/Internal/HouseholdConfigSeedApi.cs"
  - "Domain/Agents/HouseholdAgents.cs"
  - "Domain/Contracts/IHouseholdAgentSource.cs"
  - "Domain/DTOs/SharedServers.cs"
  - "Infrastructure/Agents/HouseholdAgentSource.cs"
  - "Agent/App/ChannelAgentCatalogs.cs"
  - "HouseholdMigration/Home/**"
  - "HouseholdMigration/Steps/HomeConfiguration.cs"
  - "HouseholdMigration/Steps/HomeKeys.cs"
  - "Domain/DTOs/ModelPayer.cs"
  - "Infrastructure/Memory/PaidChatClients.cs"
---

# Household configuration

Kept by the accounts service in **sections**, read by the agent and the shared servers through views.
The service's own contracts are `.claude/rules/accounts.md`.

## Sections

Five JSON documents per household (`HouseholdSections`), each with a `schemaVersion`, read and written
with the strict `SectionSchema.Json`. A section never saved reads as its defaults — never a 404. A
document's problems name the field, never its value, which may be a key.

| Section | Record | Notes |
|---|---|---|
| `settings` | `HouseholdSettings` | `name`, `timeZone`, `language` (none: follow the conversation) |
| `agents` | `AgentsSection` | `defaultAgent`; entries `{ id, template, name, customInstructions }` **or** `{ id, definition }` (a full `AgentDefinition`, **no** `telegramBotToken`) |
| `keys` | `KeysSection` | `openRouter`, `idealista`, `braveSearch`, `telegramBots: { <agent id>: { token } }`, `lemonade: { apiUrl, apiKey }` **or** `{ throughKit: true }`, never both |
| `features` | `FeaturesSection` | six bools; default all on but Idealista |
| `spaces` | `SpacesSection` | at least one space |

- An owner changes only an agent's name and custom instructions (4c): a definition entry carrying a
  `name`/`customInstructions` beside its definition is refused, so each has one place.
- A bot is keyed by the agent it is bound to; the shared deployment bot is in no section.
- A seeded definition is read by System.Text.Json, not the binder: a provider threshold is an object
  (`"preferredMaxLatency": { "p50": 1 }`), not the bare number `appsettings.json` takes through
  `ProviderThresholdConverter`. Seeding writes it so.
- **Versions**: every read and save migrates through `SectionSchema`'s steps. Changing a shape is a
  **new step appended** and the record changed beside it; an old step is never edited, since stored rows
  and operators' files were written by it. A document at a newer version than the code knows is refused,
  never read as today's.

## Secrets

- **At rest** a secret is `{"sealed": "<base64 blob>"}` in its value's place; only the schema's
  `SecretPaths` are ever sealed. Each is sealed for household + section + field
  (`HouseholdSealing.AssociatedData`) under a per-household data key wrapped by the master
  (`HouseholdDataKeys`), so a blob moved to another field or household does not open. A secret moves
  only as a plain value — inside a schema step (steps run on the opened document; the save re-seals) or
  entered again. `set-household-config` refuses a sealed blob in a file; `{"sealed": true}` keeps the
  stored value, a clear value is sealed (`HouseholdConfigCommands`).
- **The master key** is `HOUSEHOLDCONFIG__MASTERKEY` (`.env` `HOUSEHOLD_CONFIG_MASTER_KEY`), handed to
  the accounts service alone (`DeploymentConformanceTests`). **Empty** is a stack with no household
  secrets (a save carrying one refused, a sealed read `SecretsUnavailable`) — what smoke and E2E run on.
  Losing it loses every household's secrets.
- **Rotation rewraps data keys only**, never a sealed field: new key in `.env`, recreate accounts, then
  `docker exec -i accounts dotnet Accounts.dll rewrap-household-keys < previous-master.key` — the
  previous key on the input, never on the command line. It rewraps in one transaction (all or none),
  opens every sealed field to prove it, and leaves keys already under the current master, so a rerun
  changes nothing. Between the recreate and the command, secret reads answer `SecretsUnavailable`.

## Who reads what

`POST /accounts/internal/household-config/<view>` (`HouseholdConfigProtocol`). Each view is answered only
to its callers (`InternalCallers`' view table); anyone else is 403 before a row is read.

| View | Caller | Proof | Secrets unsealed |
|---|---|---|---|
| `agent` (`AgentHouseholdConfig`: settings, agents, features, spaces, `lemonade`, `disabledPeople`, `suspended` — `.claude/rules/admin-operator.md`, `accounts.md` — `ownServers`) | `agent` | names the household + its own token | OpenRouter key, Lemonade box key |
| `people`, `lemonade-boxes` (every household's, never kept) | `agent` | its own token alone, names none | each box's key |
| `openrouter-key-refused` (204, not a view; `.claude/rules/admin-keys.md`) | `agent` | as `agent` | none |
| `settings` | `mcp-scheduling`, `mcp-sugary`, `mcp-timers` | call token / assertion / delegation | none |
| `idealista` | `mcp-idealista` | call token | Idealista's two |
| `brave-search` | `household-websearch` (`InternalCallers.HouseholdWebSearch`) | its live tunnel credential **and** a call token signed for that server, acting for that household (`HouseholdProofs.OwnWebSearchAsync`) | Brave's key |
| `spaces`, `member-names` (disabled members too: their topics are kept) | `mcp-channel-signalr` | the session refresh's assertion (hub bullet below) | none |
| `agent-names` (`HouseholdAgents.Names`, never custom instructions) | `mcp-scheduling` | call token | none |
| `agent-names` | `mcp-channel-telegram`, `mcp-channel-signalr`, `mcp-sugary` | the person's assertion on the asker's channel | none |
| `sugary-people` (no disabled member; each one's grant and whether Sugary is on for them; one answer for the whole household: readers keep it per household) | `mcp-sugary` | call token (the mount) / assertion (the app) | none |
| `telegram-bots` (every household's) | `mcp-channel-telegram` | none (exception 2) | the bots' tokens |
| `sugary-off` (households turning it off, and every person an owner has not turned it on for) | `mcp-sugary` | none (exception 3) | none |

`people` (dreaming walks every household with no call behind it) is never kept, since nothing
announces a person joining.

A secret is unsealed into its own server's view and **left out** of every other, never sent sealed. A
new view is a line in `InternalCallers`' table, a route, and a row here.

**The household comes from something signed, never from a parameter** (`HouseholdProofs`,
`HouseholdConfigQuery { householdId, callToken, assertion, delegation }`):

- **A shared server** presents exactly one proof and names no household (a `householdId` is 400): the
  agent's **call token**, verified against the agent's JWKS **for the asker's own audience**
  (`TokenAudiences.CallTo`), or the accounts service's **assertion** on the asker's channel
  (`InternalCallers.AssertionChannelOf`). The household is the claims' `tid`, so a leaked server
  credential alone reads nobody's section.
- **A schedule's fire**: the scheduling server alone (`InternalCallers.Scheduling`) may present the
  **delegation** stored with the schedule (`Schedule.Delegation`), and only one of kind `schedule`. A
  schedule that delegates nothing has no proof and is read in the server's own zone
  (`.claude/rules/scheduling.md`). A delegation, not an all-households view, so the scheduling
  server's credential still opens no household's section by itself.
- **Exception 1, the agent names the household by id** — it signs every call token, so nothing it could
  present proves more. It names only the household of the identity it verified for a turn, or the one
  the work carries for extraction and dreaming (`IHouseholdConfiguration.ForAgentAsync`). It proves it **is** the agent: beside `CALLERS__agent` (=
  `AGENTAPI__SHAREDSECRET`, which SexyTime's server holds too) it presents its own token for
  `TokenAudiences.HouseholdConfiguration` (`HouseholdConfigurationClient.AgentToken`), so the shared
  secret alone opens no OpenRouter key.
- **Exceptions 2 and 3** take no proof: `telegram-bots` (Telegram polls from startup) and `sugary-off`
  (only ids: of households turning Sugary off and of people it is off for; `.claude/rules/sugary.md`).

Refusals are `HouseholdConfigRefused { reason, detail }`, never carrying a secret: 400 `BadQuery`, 404
`Unproven` / `UnknownHousehold`, 503 `SecretsUnavailable` — the reader takes 400/404 as a refusal and
a 503 as an outage. The agent may name the
home as `@home` (`HouseholdConfigProtocol.HomeHousehold`) before any turn names its id.

## The agents a household runs

Built per turn from the household's view (`HouseholdAgents.Of` over `HouseholdAgentSource`):

- **`lemonade` is never a tool server** — it is the household's Lemonade forwarder
  (`.claude/rules/tenant-kit.md`). The section refuses a definition naming it, and a turn never dials it
  whatever names it (`OwnServerEndpoints.Resolve`; `HouseholdKitLemonadeTests`).
- **A template entry** is the shipped template (`AgentTemplates`, `.claude/rules/prompts.md`) with the
  entry's id, name and custom instructions and nothing else of the owner's; a **definition entry** is
  dialled as written. An unshipped template is left out and logged, never fatal to the others.
- **No agents saved = the two templates** (`HouseholdAgents.Starting`): `jonas` (default) and `sugary`,
  so the deployment's channel defaults name an agent every household has. `nabu` is never a starting one.
- **The household's zone stamps every turn**: `AgentSpec.TimeZone` is the prompt's day and
  `MultiAgentFactory` hands it to the chat client as `turnZone` for `[Current time: …]` — a host-zone
  stamp beside a household-zone prompt gives the model two clocks. A write's bare `runAt` is read in the
  same zone (`McpAgent.Zone` → `ThreadSession`'s `localZone`), as the scheduling server reads it. Language
  fills every agent that pins none; the household's name is not in the prompt.
- **The features withhold** their `SharedServers` address from every definition — the household's, a
  person's registered agent's — and every worker (`SpawnContext.WithheldServers`). Only the household's
  own agents' endpoints present deployment credentials.
- **An own server reaches a turn only where the household set it up** (a live tunnel credential, never a
  machine's `outpost-<NAME>`): checked at **every turn's start** (`HouseholdAgents.NotSetUp`) and
  withheld by name (`OwnServerEndpoints.Resolve`), never applied to a definition — an agent outlives its
  first turn, and a server set up mid-conversation must reach the next. A worker reaches no own server
  its parent does not name (`SpawnContext.OwnServers`). The home withholds none.
- **An owner's change reaches open conversations' next turn**: `DisposableAgent.IsStaleAsync` compares
  `HouseholdAgents.AsBuilt` (key compared as a key, never revealed) and
  `ConversationGroup.RebuildIfStaleAsync` rebuilds it before the next turn, its history read back from
  the store. A person's registered agent and a worker are never
  asked.
- **A turn acting for nobody runs the home's agents** (`@home`) and reaches no household's servers.
- **Worker profiles stay the deployment's** (`subAgents`); a worker runs its parent's model and
  household.

## Who pays for a household's model calls

- **`HouseholdAgents.Payer`** is the one rule: a household with its own OpenRouter key pays with it —
  **the home too: a key saved for it takes priority over the deployment's** (user, 2026-11-13), so
  `.env` may hold none. The home with none, and code-configured agents (`HouseholdAgents.Fixed`), pay
  with the deployment's key; any other household with none gets `HouseholdWithoutKeyException` from
  `MultiAgentFactory.CreateAsync` before anything is built, answered by `ConversationGroup.WithoutKeyAnswer`
  (an `Error` then `StreamComplete`; a fire only the errors feed's `HouseholdWithoutKey`) and the group
  ended, so the first message after a key is saved runs.
- **The key travels as plain data** (`ModelPayer`, `AgentSpec.Key`, `SpawnContext.Key`,
  `JudgmentCaller.Payer`, `MemoryJudgmentContext.Payer`, `PaidChatClients`), never ambient. `ModelKey`
  prints as nothing and is never written or read as JSON.
- **Extraction and dreaming** spend nothing for a household with no key, one unreadable just now, one
  suspended, or a disabled person — never on another's key.
- **Housekeeping stays the deployment's**; a shared server's Jev asks on the server's own key, naming
  the household for the figures (`JudgmentCaller.For(context)`). A household key OpenRouter refuses is
  logged once per household by the judge, never the key.

## Saves are announced; readers keep briefly

Writer and readers share this contract (`HouseholdConfigProtocol`):

- A save publishes `HouseholdConfigSaved { householdId, section }` on `accounts:household-config:saved`
  (`RedisAnnouncer`). So do a suspension (`HouseholdConfigProtocol.SuspensionChanged`), a member
  disabled or re-enabled (`HouseholdConfigProtocol.PeopleChanged`) and a tunnel credential minted or
  revoked (`HouseholdConfigProtocol.OwnServersChanged`, by `TunnelCredentialStore`) — none a section. A
  reader drops **all** it kept of the household whatever section is named.
- The save stands if nobody could be told; the command exits 1, the admin route warns
  (`AdminSections.NotToldWarning`).
- **The reader** is `IHouseholdConfiguration` / `HouseholdConfigurationClient`, registered by
  `AddHouseholdConfiguration(accountsConfig, credential, agentToken?)`. The `householdId` a shared
  server passes is the one **it** verified; an answer for another household is refused, not kept.
  Kept per (view, household) for `Accounts:ConfigurationLifetime` (1 minute) or until a heard save; the
  across-household entries (`telegram-bots`, `sugary-off`) drop on any household's `keys`/`features`
  save or people change. An answer asked while a save was announced is returned but not kept. A refusal is
  `HouseholdConfigRefusedException`; an outage or a 503 is `HttpRequestException`.
- Idealista and Sugary are given no Redis for this, deliberately: a minute is "without a restart", and
  Sugary keeps off Redis (`docs/adr/0043`). A kit's web search has a Redis, but the kit's, where no save is
  announced (`hearSaves: false`), so it too reads a new key within the minute.

## A shared server's accessor

Tool code never holds the reader: it reads its section through **one accessor**,
`Mcp.Hosting/CallHousehold.cs` (`CallHousehold : ICallHousehold`) — `call.Household` (the call token
`CallTokenGate` verified), `CallHousehold.OfPerson` or `CallHousehold.OfFire`.
`CallHouseholdConformanceTests` fails on tool code naming the reader or a proof, a call-token proof
built anywhere else, or the verified token read past it. A refusal (`HouseholdConfigRefusedException`)
or an outage (`HttpRequestException`) is never mistaken for a household with nothing set.

## Operator commands and seeding

- The admin area (`.claude/rules/admin.md`) saves through the same `HouseholdConfigStore.SaveAsync` and
  schema as `get-household-config` / `set-household-config` (`HouseholdConfigCommands`).
  `set-household-config --file -` reads the input, so no secret sits in a file in the volume.
- The deployment holds no Idealista key and a kit no Brave key: the home's go in its `keys` section.
- **Seeding** is `POST /accounts/internal/household-config-seed` (`HouseholdConfigSeedApi`,
  `{ householdId, section, document, dryRun }` → `{ saved, alreadySaved, kept }`, 400 with the problems),
  for `CALLERS__household-migration` alone. A section already saved is left as it is (a rerun changes
  nothing; an owner's edits are never put over); **`keys` is the exception**: each seeded key it lacks is
  added, every held one kept, saving only when something was given (`kept` names those left). The document is laid over the section as the household reads it now, field
  by top-level field, so what it leaves out stays the household's. A secret on a service with no master
  key is 503, dry run or not. A failure to tell readers is ignored (nothing read the section before).
- **Telegram** re-reads `telegram-bots` every `Bots:Refresh` (`.claude/rules/telegram.md`).
- **The hub** (`ChatHub.GetSpaces`, `JoinSpace`, `GetAgents`, `ValidateAgent`) proves with the **session
  refresh's** assertion (`ISessionAccounts.SessionAsync`), not the connection's, which lives ten minutes
  (`.claude/rules/chat-hub.md`, "Spaces"). The picker's `agent-names` are the household's agents with
  their patchable models and model facts taken from the catalogue the agent registered
  (`AgentCatalogBuilder.ForHousehold`).

## Tests

`HouseholdConfigurationTests`, `HouseholdConfigurationClientTests` (real Redis). Pub/sub ignores the
leased database, so a test listens for its own household's saves only.
