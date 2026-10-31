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
  - "Agent/App/HomeAgentCatalog.cs"
  - "HouseholdMigration/Home/**"
  - "HouseholdMigration/Steps/HomeConfiguration.cs"
  - "HouseholdMigration/Steps/HomeTelegramBots.cs"
---

# Household configuration

What a household *is* (CONTEXT.md, "Household configuration") is kept by the accounts service, in
**sections**, and read by the agent and the shared servers through views
(`.scratch/tenants-config/spec.md`, "Configuration in the accounts service"). The accounts service's
own contracts are `.claude/rules/accounts.md`; this file is the configuration's.

## Sections

Five, each one JSON document per household with a `schemaVersion` (`Domain/DTOs/Household`,
`HouseholdSections`): the records there are the **current** shape, read and written with
`SectionSchema.Json` (web casing, an unknown field refused, a null where the shape has none
refused). A household that never saved a section reads it as its defaults — never a 404 — so a new
household is readable before anything is seeded.

| Section | Record | v1 shape | Default |
|---|---|---|---|
| `settings` | `HouseholdSettings` | `name` (1–200), `timeZone` (an IANA zone this host knows), `language` (a tag such as `es`, or none: agents follow the conversation) | the tenant row's name, the service's local zone (compose's shared `TZ`), no language |
| `agents` | `AgentsSection` | `defaultAgent` (one of the ids below, or none), `agents`: each `{ id, template, name, customInstructions }` **or** `{ id, definition }` — a full `AgentDefinition` whose `id` is the entry's and which carries **no** `telegramBotToken` | none at all: accounts knows no template's body (06 decides what a household with none runs) |
| `keys` | `KeysSection` | `openRouter: { apiKey }`, `idealista: { apiKey, secret }`, `telegramBots: { <agent id>: { token } }` — every leaf a secret | nothing set |
| `features` | `FeaturesSection` | `scheduling`, `timers`, `sugary`, `idealista`, `memory`, `workers`, each a bool | all on but Idealista, which needs the household's own key |
| `spaces` | `SpacesSection` | `spaces`: each `{ slug, name, accentColor, defaultAgent, order }`, slugs and colours as `SpaceConfig` checks them, at least one | the one `default` space, "Main", `#e9601f` |

An agent id is a slug (`HouseholdSections.IsAgentId`). An owner changes only an agent's name and
custom instructions (4c): on a template entry its `name`/`customInstructions`, on a definition entry
the definition's own — a definition entry carrying either beside it is refused, so each has one
place. A bot is keyed by the agent it is bound to, so a household holds one bot per agent; the
shared deployment bot is not in any section (its token is the deployment's).

A seeded definition is read by System.Text.Json, not the configuration binder: a provider threshold
is an object (`"preferredMaxLatency": { "p50": 1 }`), not the bare number `appsettings.json` takes
through `ProviderThresholdConverter`. 06's seeding writes it so.

## Versions

`SectionSchema` holds a section's steps: step *n* carries a plain document from version *n* to
*n + 1*, and the current version is the number of steps. Version 0 is a document naming no version,
which the first step (`SectionSteps.Unversioned`) reads as the first shape. **Every read and every
save** migrates: the stored document's sealed fields are opened, the plain document is migrated and
written out whole (`Normalize`, every default filled), and a save seals the schema's secrets again
for their places in the new shape — so a step is a pure function of the plain document and may move
a secret like any field. A document at a newer version than the code knows is refused, never read as
today's. Changing a shape is a **new step appended** and the record changed beside it; an old step is
never edited, since stored rows and operators' files were written by it.

## Secrets

- **At rest** a secret is `{"sealed": "<base64 blob>"}` where its value was, so a stored document
  says for itself what is sealed in whatever shape it was written. The schema names its secrets
  (`SecretPaths`, `*` for any one name: `telegramBots/*/token`); nothing else is ever sealed.
- **The associated data** is one convention (`HouseholdSealing.AssociatedData`): household, section
  and field path (property names joined by `/`, escaped as a JSON pointer's), so a blob moved to
  another household, section or field — one bot's token under another agent — does not open.
- **The data key** is a random `SealingKey` per household (`HouseholdDataKey`, one row each), minted
  when its first secret is sealed and kept only wrapped: `master.Seal(dataKey.Export(),
  AssociatedData(household, "data-key", ""))` — `data-key` is a section no section is named. The row
  records which master wrapped it (`MasterKeyId`, the start of the master's SHA-256), so a service
  started with another master says so instead of failing every secret.
- **The master key** is `HOUSEHOLDCONFIG__MASTERKEY` (`.env` `HOUSEHOLD_CONFIG_MASTER_KEY`, the base64
  of 32 random bytes), the phase's one new deployment secret, handed to the accounts service alone
  (`DeploymentConformanceTests`). Malformed stops the service and every command. **Empty** is a stack
  with no household secrets: sections without one save and read, a save carrying one is refused, a
  read of a sealed one is `SecretsUnavailable` — which is what the smoke and E2E stacks run on until
  they need keys. Losing it loses every household's secrets; owners enter them again.
- **Rotation rewraps data keys only**, never a sealed field: put the new key in `.env`, recreate
  accounts, then `docker exec -i accounts dotnet Accounts.dll rewrap-household-keys <
  previous-master.key` — the previous key on the input, never on the command line. It rewraps every
  data key the previous key wrapped in one transaction (all or none), opens every sealed field to
  prove it, and leaves a key already under the current master as it is, so a rerun changes nothing.
  Between the recreate and the command, reads of secrets answer `SecretsUnavailable`.

## Who reads what

`POST /accounts/internal/household-config/<view>` (`HouseholdConfigProtocol`, `Internal/HouseholdConfiguration`),
behind the internal API's caller filter like every route there. One view per route, answered only to
the callers it is for (`InternalCallers`' view table) — anyone else is 403 before a row is read:

| View | Caller (`CALLERS__<name>`) | Answer | Secrets unsealed |
|---|---|---|---|
| `agent` | `agent` | `AgentHouseholdConfig`: id, `isHome`, settings, agents, features, spaces | the OpenRouter key |
| `settings` | `mcp-scheduling`, `mcp-sugary` | `HouseholdConfigAnswer<HouseholdSettings>` | none |
| `idealista` | `mcp-idealista` | `HouseholdConfigAnswer<IdealistaKeys?>` | Idealista's two |
| `spaces` | `mcp-channel-signalr` | `HouseholdConfigAnswer<SpacesSection>` | none |
| `telegram-bots` | `mcp-channel-telegram` | `HouseholdTelegramBots`: every household's bots | the bots' tokens |
| `agent-names` | `mcp-channel-telegram` | `HouseholdConfigAnswer<HouseholdAgentNames>`: each agent's id and name and the default (`HouseholdAgents.Names`, the entries its turns are built from: the two templates for a household that saved none, an unshipped template left out), proven by the person's assertion on `telegram` | none |

A secret is unsealed into its own server's view and **left out** of every other, never sent sealed.
A new view, or a server that comes to need one, is a line in that table, a route, and a row here.

**The household comes from something signed, never from a parameter** (`HouseholdProofs`). The query
is `HouseholdConfigQuery { householdId, callToken, assertion, delegation }`:

- **A shared server** presents exactly one proof and names no household (a `householdId` is 400):
  the agent's **call token** it was called with — verified against the agent's JWKS (`AgentKeysUrl`,
  compose's `AGENTKEYSURL`, on the wall clock) **for the asker's own audience**
  (`TokenAudiences.CallTo(ServerAddress.Named(<caller name>))`), so a token handed to one server
  proves nothing to another — or the accounts service's own **assertion** on the asker's channel
  (`InternalCallers.AssertionChannelOf`: the hub `signalr`, Telegram `telegram`, Sugary `sugary`), for
  a server serving a person rather than a call (the hub's connection, Sugary's app). The household
  is the claims' `tid`; a token naming nobody proves none. So a leaked server credential alone
  reads nobody's section, and with a token it reads that one household's own section.
- **A schedule's fire** has no call behind it, so the scheduling server alone (`InternalCallers.Scheduling`;
  anyone else is 400) may present instead the **delegation** stored with the schedule
  (`Schedule.Delegation`): verified against the agent's JWKS for `TokenAudiences.Delegation`, and only
  a delegation of kind `schedule` (a watch's or a login's is 404 `Unproven`). It is the agent's word,
  signed while that person's turn wrote the schedule, for the person behind it — so their `tid` is the
  schedule's household. A schedule that delegates nothing has no proof, and is read in the server's
  own zone (`.claude/rules/scheduling.md`). Rather than an all-households view: the scheduling
  server's credential still opens no household's section by itself.
- **Exception 1, the agent names the household by id.** It signs every call token there is, so
  nothing it could present proves more than its word: for a turn it names the household of the
  identity it verified, and for work with no call behind it — memory extraction, dreaming walking
  households — the household the work carries. What it proves is that it **is** the agent: beside
  `CALLERS__agent` (which is `AGENTAPI__SHAREDSECRET`, a secret SexyTime's server holds too) it
  presents a token of its own signed for `TokenAudiences.HouseholdConfiguration`, acting for nobody
  (`HouseholdConfigurationClient.AgentToken(callTokens)`), so the shared secret alone opens no
  household's OpenRouter key.
- **Exception 2, Telegram reads every household's bots.** It long-polls its bots from startup,
  before any call could name a household, so `telegram-bots` takes no proof and answers every
  household's (a household whose secrets do not open is left out and logged, not fatal to the rest).
  It is cached as one entry, dropped by any household's `keys` save.

Refusals: 400 `BadQuery` (no proof, two, a shared server naming a household, or a delegation from
anyone but scheduling), 404 `Unproven` (the
detail says why: a `TokenRefusal`, another server's audience, another channel, nobody) or
`UnknownHousehold`, 503 `SecretsUnavailable` (no master, or a data key under another), each a
`HouseholdConfigRefused { reason, detail }` and never carrying a secret.

The agent may name the home household as `@home` (`HouseholdConfigProtocol.HomeHousehold`; an id is 32
hex characters, so no household is called that) and is answered the home's view under its real id:
it asks so for what is the home's before any turn names an id — the catalogue it registers with its
channels, a turn acting for nobody. Its reader keeps that answer under the name and drops it on any
household's save, since a save names the home by an id the reader may not have been told.

## The agents a household runs

The agent builds a turn's agents from the turn's household's view (`HouseholdAgents.Of`, Domain, over
`HouseholdAgentSource`, the agent's `IHouseholdAgentSource`):

- **An entry is a template or a definition.** `{ id, template, name, customInstructions }` is the
  shipped template (`AgentTemplates`, `Domain/Prompts/Agents/<name>.md`, `.claude/rules/prompts.md`) under
  the entry's id, with the entry's name and custom instructions and nothing else of the owner's; it
  reaches only those of its servers the deployment runs as shared servers. `{ id, definition }` is its
  definition as seeded (the home's jack, jonas, nabu and sugary), dialled as written. An entry naming a
  template this build does not ship is left out and logged, not fatal to the household's other agents.
- **A section that names no agent is the two templates** (`HouseholdAgents.Starting`): `jonas`, the
  barebones Jonas, and `sugary`, under their own ids and names, `jonas` answering by default — so a new
  household has agents before anything is saved, and the deployment's channel defaults (`sugary` for
  the Sugary app) name an agent it has.
- **The settings fill the prompt.** The language is every agent's that pins none (an agent's own pin
  wins: nabu stays Spanish); the zone is the day's (`AgentSpec.TimeZone` → the `date` section's `zoned`
  part, named only when it is not the host's own, so the home's prompts are the snapshots'), and the
  zone a session reads a write's bare time in. The household's name is not in the prompt: an agent's
  name is the entry's.
- **The features withhold.** `sharedServers` in the agent's settings (`SharedServers`) maps each
  feature to the address the deployment runs its server at; a feature turned off drops that address
  from every definition — the household's, a person's registered agent's — and from every worker its
  agents spawn (`SpawnContext.WithheldServers`), and `memory` off drops the `memory` feature, `workers`
  off the `subagents` one. Only the household's own agents' endpoints present deployment credentials.
- **A turn acting for nobody runs the home's agents** (`@home`) and reaches no household's servers.
- **The worker profiles stay the deployment's** (`subAgents` in the agent's settings): a worker runs its
  parent's model for its parent's household, and the household's `workers` feature decides whether
  its agents may spawn one at all.

## Saves are announced; readers keep briefly

A save publishes `HouseholdConfigSaved { householdId, section }` on Redis's
`accounts:household-config:saved` (`RedisAnnouncer`, the same lazily opened connection that
announces tunnel revocations). The save stands if nobody could be told; the command says so and
exits 1.

**The reader** is `IHouseholdConfiguration` (`Domain/Contracts`), implemented by
`HouseholdConfigurationClient` (`Infrastructure/Clients/Accounts`), registered by
`AddHouseholdConfiguration(accountsConfig, credential, agentToken?)` — the agent passes its
`CALLERS__agent` credential and `sp => HouseholdConfigurationClient.AgentToken(sp.GetRequiredService<CallTokens>())`;
a shared server its own `MCP__SHAREDSECRET` and nothing else.

- `ForAgentAsync(householdId)`, `SettingsAsync(householdId, proof)`, `IdealistaKeysAsync(householdId,
  proof)` (null: none set), `SpacesAsync(householdId, proof)`, `AgentNamesAsync(householdId, proof)`, `TelegramBotsAsync()`. `HouseholdProof.OfCallToken`
  / `.OfAssertion` / `.OfDelegation`. The `householdId` a shared server passes is the one **it** already verified (the
  call's `tid`); it is the cache key, and an answer for any other household is refused, not kept.
- Kept per (view, household) for `Accounts:ConfigurationLifetime` (1 minute) and dropped the moment a
  save of that household is heard on the host's own `IConnectionMultiplexer`, subscribed before the
  first ask. An answer asked while a save was announced is returned but not kept. A host with no
  Redis — Idealista and Sugary — keeps for the lifetime alone; one whose Redis is away retries
  the subscription once a lifetime. Idealista and Sugary are given no Redis for this, deliberately:
  a saved key or zone reaching them within a minute is "without a restart", and Sugary's log keeps
  off Redis (`docs/adr/0043`). Scheduling has its own Redis, so a save reaches it at once.

## A shared server's accessor

A shared server never holds the reader in its tool code. It reads its own section of the household a
call acts for through **one accessor**, `Mcp.Hosting/CallHousehold.cs` (`CallHousehold :
ICallHousehold`, `Domain/Contracts`), beside `CallTurn`:

- **`call.Household`** (`CallTurn`): null when the call acts for nobody or names no household;
  otherwise the verified identity's `tid` and, as its proof, the very call token `CallTokenGate`
  verified, which the gate leaves on the call's `Items` (`CallTurn.VerifiedTokenKey`) only once it
  verified. `SettingsAsync(ct)`, `IdealistaKeysAsync(ct)`; `ICallHousehold.ZoneAsync(fallback, ct)`
  (`CallHouseholds`) is the zone its dates are read in — the server's own for a call for nobody or a
  zone this host's time zone database does not know. The reader is resolved from the call's services
  when a section is asked, so every filesystem server can hand its mount the call's household
  (`FileSystemCaller.Household`) and only those that read configuration register
  `AddHouseholdConfiguration`; one that did not and asks is an `InvalidOperationException` naming it.
- **`CallHousehold.OfPerson(householdId, assertion, reader)`**: a person a server's own app serves, on
  the assertion its request carried (Sugary's `HomeCalendar.BindAsync`).
- **`CallHousehold.OfFire(householdId, delegation, reader)`**: a schedule's fire, on its delegation.

Who reads what through it: Idealista searches on the household's own keys (`IdealistaClients`, one
client per key pair, so each account's token is its own; no keys is an `authentication` tool error and
nothing is searched — the deployment holds no Idealista key at all, so on the deploy that removed
`IDEALISTA__*` from `.env` the operator puts the home's keys into its `keys` section with
`set-household-config`, and until then the home's searches say none is set); scheduling reads and fires in the
household's zone; Sugary reads her times and her calendar's days in it. `CallHouseholdConformanceTests`
fails on tool code (a server's tool types, `Domain/Tools`) naming the reader or a proof, on a call
token's proof built anywhere but the accessor, and on the verified token read past it.
- `HouseholdConfigRefusedException` for a refusal, `HttpRequestException` for an outage or a 503: a
  reader never mistakes either for a household with nothing set.

## Operator commands

Until the admin area (4c): `get-household-config --household <id> --section <name>` prints the
section at its current shape with every secret as `{"sealed": true}`; `set-household-config
--household <id> --section <name> --file <path|->` (`-` is the input: `docker exec -i accounts dotnet
Accounts.dll set-household-config ... --file - < keys.json`, so no secret sits in a file in the
volume) saves a document of any known version, sealing each secret written in clear and keeping the
stored one wherever the file says `{"sealed": true}` — so get, edit, set round-trips without retyping
keys. A sealed blob in a file is refused (it was sealed for elsewhere). Exit 2 for an unknown section
or a document that is not one (each problem listed, never a value), 1 for a household that does not
exist, secrets with no master, or readers not told. Saving `settings` also renames the tenant row.
`--household home` names the home household, for a stack being set up that does not know its id yet
(the smoke stack seeds the home's agents so, `scripts/smoke-stack.sh up`).

## For the tickets that build on it

- **06** (done): "The agents a household runs" above; the agent reads with `CALLERS__agent`
  (`AGENTAPI__SHAREDSECRET`) and its own token. The household migration's `HomeConfiguration` seeds
  the home's settings (the deployment's zone, no language), its agents (`HouseholdMigration/Home/agents.json`,
  today's definitions, a threshold an object) and its features (every one on, Idealista too) through
  the seed route below; the bots are 13's.
- **07**: `AgentHouseholdConfig.IsHome` picks the deployment key; `OpenRouterApiKey` null refuses the
  turn. Dreaming needs a list of households and their people, which is not here yet.
- **08** (done): the accessor above (`CallHousehold`), its fire proof and the three `CALLERS__` entries.
- **12** (done): the hub (`ChatHub.GetSpaces`, `JoinSpace`) proves with the **session refresh's**
  assertion (`ISessionAccounts.SessionAsync`, as every turn does), not the one the connection arrived
  with, which lives ten minutes while a chat client stays connected for hours. The chat client's host
  keeps no list and reads no header; see `.claude/rules/channels.md`, "Spaces".
- **Seeding a section the household never saved** is `POST /accounts/internal/household-config-seed`
  (`HouseholdConfigSeedApi`, `{ householdId, section, document, dryRun }` → `{ saved, alreadySaved }`,
  400 with the problems), admitted for `CALLERS__household-migration` alone like the import. A section
  the household already saved is left as it is, so a rerun changes nothing and an owner's own edits
  are never put over; a dry run checks the document and writes nothing. The document is laid over the
  section as the household reads it now, top-level field by field, so what it leaves out stays the
  household's (the settings' name its row carries, the features it does not name). Any section: 06 seeds the
  agents through it as 12 seeds the spaces (`HouseholdMigration/Steps/HouseholdSpaces`). It tells
  readers as any save does but ignores a failure to, since nothing has read a section before it exists.
  A document carrying a secret on a service with no master key is 503, dry run or not, with the reason
  (the smoke stack runs so: a migration given no bots seeds no keys and needs none).
- **06**, from 08 (done): a session reads a write's bare `runAt` in the household's zone (`McpAgent.Zone`
  into `ThreadSession`'s `localZone`), as the scheduling server does.
- **07**: `HouseholdAgents.Configuration` carries the view a turn's agents were built from — `IsHome`,
  `OpenRouterApiKey` — beside the spec; `MultiAgentFactory.CreateAsync` has it in hand where the chat
  client is made. Memory extraction's request names its household (`TenantId`), which the worker
  already reads its agent by.
- **13** (done): the Telegram server reads `telegram-bots` at its start and every `Bots:Refresh` (five
  seconds; the reader's cache makes that free, and a `keys` save drops it), starting and stopping each
  household's bots as the answer changes, and `agent-names` for `/agent` on the asking person's
  assertion (`.claude/rules/channels.md`, Telegram). The household migration's `HomeTelegramBots` seeds
  the home's jack and jonas bots into its `keys` through the seed route, from tokens piped into the run
  (`--telegram-bots -`). Because the seed route leaves a saved section alone, the deploy runs the
  migration with the bots **before** any `keys` save (08's Idealista key included), or adds the bots to
  the saved section with `set-household-config`.

## Tests

Seam 1: `HouseholdConfigurationTests` drives the routes raw and the commands through `AccountsServer`
(a master key by default, `AgentKeysUrl` at `TestJwks`, call tokens from `McpTestSecret.CallTokens`,
assertions from the server's own `Assertions`; a second server over the same directory for the
rotation); `HouseholdConfigurationClientTests` drives the client against a running service and a
real Redis (`RedisFixture`), changing a row behind the service's back to see what is kept.
Pub/sub ignores the leased database, so a test listens for its own household's saves only.
