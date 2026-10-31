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

A secret is unsealed into its own server's view and **left out** of every other, never sent sealed.
A new view, or a server that comes to need one, is a line in that table, a route, and a row here.

**The household comes from something signed, never from a parameter** (`HouseholdProofs`). The query
is `HouseholdConfigQuery { householdId, callToken, assertion }`:

- **A shared server** presents exactly one proof and names no household (a `householdId` is 400):
  the agent's **call token** it was called with — verified against the agent's JWKS (`AgentKeysUrl`,
  compose's `AGENTKEYSURL`, on the wall clock) **for the asker's own audience**
  (`TokenAudiences.CallTo(ServerAddress.Named(<caller name>))`), so a token handed to one server
  proves nothing to another — or the accounts service's own **assertion** on the asker's channel
  (`InternalCallers.AssertionChannelOf`: the hub `signalr`, Telegram `telegram`, Sugary `sugary`), for
  a server serving a person rather than a call (the hub's connection, Sugary's app). The household
  is the claims' `tid`; a token naming nobody proves none. So a leaked server credential alone
  reads nobody's section, and with a token it reads that one household's own section.
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

Refusals: 400 `BadQuery` (no proof, two, or a shared server naming a household), 404 `Unproven` (the
detail says why: a `TokenRefusal`, another server's audience, another channel, nobody) or
`UnknownHousehold`, 503 `SecretsUnavailable` (no master, or a data key under another), each a
`HouseholdConfigRefused { reason, detail }` and never carrying a secret.

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
  proof)` (null: none set), `SpacesAsync(householdId, proof)`, `TelegramBotsAsync()`. `HouseholdProof.OfCallToken`
  / `.OfAssertion`. The `householdId` a shared server passes is the one **it** already verified (the
  call's `tid`); it is the cache key, and an answer for any other household is refused, not kept.
- Kept per (view, household) for `Accounts:ConfigurationLifetime` (1 minute) and dropped the moment a
  save of that household is heard on the host's own `IConnectionMultiplexer`, subscribed before the
  first ask. An answer asked while a save was announced is returned but not kept. A host with no
  Redis — Idealista and Sugary today — keeps for the lifetime alone; one whose Redis is away retries
  the subscription once a lifetime.
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

## For the tickets that build on it

- **06** (agents per household): seed the home's settings, agents (jack, jonas, nabu, sugary as
  definition entries, bots moved to keys) through the store's `SaveAsync` — a migration endpoint for
  `CALLERS__household-migration` beside `household-import` is the natural way, re-runnable (skip a
  section already saved) with a dry run. Register `AddHouseholdConfiguration` in the agent and in
  `Tests/E2E/Fixtures/WebChatStack.cs`'s stack (its accounts container then needs `AGENTKEYSURL`
  pointed at its agent). Decide what a household with an empty agents section runs (the two
  templates).
- **07**: `AgentHouseholdConfig.IsHome` picks the deployment key; `OpenRouterApiKey` null refuses the
  turn. Dreaming needs a list of households and their people, which is not here yet.
- **08**: compose gains `CALLERS__mcp-idealista`, `CALLERS__mcp-scheduling`, `CALLERS__mcp-sugary`
  fed from each server's `MCP_SECRET_*` (update `EveryChannelCallerOfTheAccountsService_IsOneItKnowsTheChannelOf`
  and `TheAccountsServicesCallers_PresentTheirOwnMcpCredential`); `CallTurn` exposes the verified
  claims but not the raw call token, which the accessor must forward; Idealista and Sugary have no
  Redis yet. A schedule fires with no call: its stored delegation (agent-signed, `tid`) is the
  natural proof, which is a new proof kind here (`TokenAudiences.Delegation`, kind `schedule`) —
  add it to `HouseholdProofs` for `mcp-scheduling` rather than an all-households view.
- **12** (done): the hub (`ChatHub.GetSpaces`, `JoinSpace`) proves with the **session refresh's**
  assertion (`ISessionAccounts.SessionAsync`, as every turn does), not the one the connection arrived
  with, which lives ten minutes while a chat client stays connected for hours. The chat client's host
  keeps no list and reads no header; see `.claude/rules/channels.md`, "Spaces".
- **Seeding a section the household never saved** is `POST /accounts/internal/household-config-seed`
  (`HouseholdConfigSeedApi`, `{ householdId, section, document, dryRun }` → `{ saved, alreadySaved }`,
  400 with the problems), admitted for `CALLERS__household-migration` alone like the import. A section
  the household already saved is left as it is, so a rerun changes nothing and an owner's own edits
  are never put over; a dry run checks the document and writes nothing. Any section: 06 seeds the
  agents through it as 12 seeds the spaces (`HouseholdMigration/Steps/HouseholdSpaces`). It tells
  readers as any save does but ignores a failure to, since nothing has read a section before it exists.
- **13**: `TelegramBotsAsync` at startup and again when it changes; `/agent` needs a household's
  agent list on `telegram`, a new view.

## Tests

Seam 1: `HouseholdConfigurationTests` drives the routes raw and the commands through `AccountsServer`
(a master key by default, `AgentKeysUrl` at `TestJwks`, call tokens from `McpTestSecret.CallTokens`,
assertions from the server's own `Assertions`; a second server over the same directory for the
rotation); `HouseholdConfigurationClientTests` drives the client against a running service and a
real Redis (`RedisFixture`), changing a row behind the service's back to see what is kept.
Pub/sub ignores the leased database, so a test listens for its own household's saves only.
