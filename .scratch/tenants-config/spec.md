# Tenants 4b: tenant config

Status: ready-for-agent

## Problem Statement

Ziggurat has households and people, signed identity and, after 4a, a way for a household's own
servers to join from its own site — but everything a household *is* still comes from the
deployment's files. Its agents are four entries in the agent's settings, its spaces a list in the
chat client's settings, its Telegram bots two tokens in compose, its keys the deployment's own. Every
model call is paid by the operator's OpenRouter key. Topics, schedules, custom agents, outposts and
metrics are deployment-wide, so a second household's data would sit in the same lists as the
first's. The time zone a schedule is read in is the machine's. A timer can only ring on a satellite,
which a household without satellites does not have.

A second household cannot be given anything of its own without editing the deployment.

## Solution

Each household has its own configuration, kept by the accounts service in sections — its settings
(name, time zone, language), its agents, its keys, its features, its spaces — with its secrets sealed
under a key of its own. The agent builds a household's agents from its configuration and pays for
every model call made for that household with the household's own OpenRouter key. Shared servers ask
for their own section of the calling household's configuration. Everything a household shares or
the deployment counts is kept under the household's id, and today's data moves into the home
household.

Prompts and skills become text documents shipped in the repo, so a later editor can work on them;
for now a household changes only its agents' names and custom instructions. A new household starts
from two shipped templates, a barebones Jonas and Sugary; jack, jonas, nabu and sugary become the
home household's own agents. The eval keeps its own copies of the agents it drives.

One shared Telegram bot serves every household, beside the home's own bots. Timers ring on a
household's satellites when it has them and as an insistent alert on the person's channels when it
does not. Lemonade's use is counted per household.

## User Stories

### People

1. As a person, I want my household's assistant to have the name my household gave it, so that it
   is ours and not the operator's.
2. As a person, I want the assistant to follow the custom instructions my household set, so that it
   behaves the way we asked.
3. As a person, I want schedules and dates read in my household's time zone, so that a reminder at
   eight rings at eight where I live.
4. As a person, I want the assistant to answer in my household's language by default, so that I do
   not have to ask every time.
5. As a person, I want to see only my household's topics, schedules and spaces, so that another
   household's data never appears in my lists.
6. As a person, I want my household's spaces with their names and colours, so that the chat client
   is organised the way we chose.
7. As a person, I want a space to open with its own default agent, so that the kitchen space talks
   to the cooking agent without my picking it.
8. As a person, I want to message the shared Telegram bot once I am linked and reach my household's
   default agent, so that I need no bot of my own.
9. As a person, I want to switch the agent I talk to on the shared bot with a command, so that I can
   reach any of my household's agents from one chat.
10. As a person in the home household, I want to keep talking to the jack and jonas bots, so that
    nothing changes for us.
11. As a person, I want a timer to ring on my phone or Telegram when my household has no satellites,
    so that timers work for every household.
12. As a person, I want to dismiss a ringing timer from any channel it rings on and have it stop
    everywhere, so that I do not silence it three times.
13. As a person, I want my timers to keep ringing on my satellites at home, so that the kitchen timer
    still rings in the kitchen.
14. As a person, I want my custom agents to survive a restart, so that an agent I made is still there
    tomorrow.
15. As a person, I want a turn to refuse with a clear message when my household has no OpenRouter
    key set, so that I know why nothing answers.
16. As a person, I want my memories extracted and consolidated with my household's key, so that my
    household pays for its own memory.

### The household's owner

17. As an owner, I want my household's OpenRouter key to pay for every model call made for us —
    turns, workers, memory, skill preloading, screening — so that our use is ours to pay.
18. As an owner, I want my household's keys sealed so that only the server a key belongs to ever
    sees it in the clear, so that one server's leak does not leak every key.
19. As an owner, I want my household to start with a barebones Jonas I can name and a Sugary agent,
    so that we have something useful from the first sign-in.
20. As an owner, I want to turn shared features on or off for my household (scheduling, timers,
    Sugary, Idealista, memory, workers), so that our agents only offer what we use.
21. As an owner, I want my household's Idealista key used for our property searches, so that we pay
    for our own searches.

### The operator

22. As the operator, I want each household's configuration kept in one place with a schema version,
    migrated forward automatically, so that changing a section's shape never breaks an old household.
23. As the operator, I want one new deployment secret — the master key that wraps each household's
    data key — and nothing per household in `.env`, so that adding a household is not a deploy.
24. As the operator, I want rotating the master key to rewrap data keys only, so that rotation does
    not re-encrypt every secret.
25. As the operator, I want a saved section to reach every server that cached it within moments, so
    that a key change takes effect without restarts.
26. As the operator, I want to set any section of a household's configuration from a command until
    the admin area exists, so that I can configure the home household now.
27. As the operator, I want the home household's model calls paid by the deployment's key, so that
    my own household needs no separate key.
28. As the operator, I want today's data — topics, schedules, custom agents, outposts, metrics,
    spaces, agents, bots — moved into the home household by the household migration, re-runnable and
    with a dry run, so that the move is safe.
29. As the operator, I want metrics and Lemonade use (speech seconds, speech characters, embeddings)
    counted per household, so that I can see who uses what.
30. As the operator, I want the deployment's own housekeeping calls (keep-alives, model capabilities)
    to stay on the deployment key, so that a household with no key does not break them.

### Developers

31. As a developer, I want prompt sections and skills as text documents with a small header
    (purpose, priority, budget, audience, claims) and host-filled placeholders, so that the prompt is
    data a later editor can work on.
32. As a developer, I want the conversion to documents to change no composed prompt, so that the
    snapshots prove it.
33. As a developer, I want the budget, staleness and snapshot tests to run over the documents, so
    that the prompt's guarantees survive the move.
34. As a developer, I want the shipped agent templates as documents beside the prompts, so that a
    new household's agents come from the repo.
35. As a developer, I want the eval to drive its own copies of today's agents under the tests, so
    that the eval does not depend on any household's configuration.
36. As a developer, I want every shared store's key to carry the household id, so that a store cannot
    answer one household with another's data.
37. As a developer, I want background work outside a turn (memory extraction, dreaming) to carry the
    household it works for, so that it picks the right key without ambient context.
38. As a developer, I want a shared server to read its section of the calling household's
    configuration through one accessor over the verified call, so that no tool reads configuration
    around the household check.

## Implementation Decisions

### Configuration in the accounts service

- A household's configuration is a set of sections, each one JSON document with a `schemaVersion`:
  **settings** (name, time zone, language), **agents**, **keys**, **features**, **spaces**. The
  accounts service migrates a section forward on read and on save; a section's migrations are code
  beside its schema. Stored in the provider-neutral EF store.
- Secrets inside a section are sealed fields: each household has a random data key; secrets are
  sealed with AES-GCM under it, with the household and field as associated data; the data key is
  wrapped by the deployment master key, the one new `.env` secret, given to the accounts service
  alone. Rotating the master rewraps data keys. The sealing code moves into Domain beside the
  signing code, and remembered logins reuse it.
- An internal API answers a caller its own view: the agent reads a household's settings, agents,
  features and spaces and its OpenRouter key; a shared server reads only its own section with its
  own secrets unsealed. Callers are the existing per-service credentials. The household comes from
  the verified token or assertion of the call that needs it, never from a parameter a caller picks.
- A save publishes an invalidation over Redis (the accounts service gains a connection); readers
  cache briefly and drop a household's entry on its message.
- Until the admin area (4c), an operator command reads and sets any section of a household's
  configuration from a file, sealing secrets on the way in.

### Prompts, skills and templates as documents

- Each prompt section and skill becomes a text document — markdown with a small header carrying
  what its declaration carries today (purpose, priority band, token budget, audience, claims) — and
  the manifest is read from the documents. Dynamic content (the date, an agent's name and
  description, the language, the time zone, satellites, printer formats, mounts, setup summaries)
  stays host-filled through named placeholders a document cannot drop.
- Servers keep serving their own prompts and skills, now from documents. The conversion changes no
  composed prompt or skill body: the committed snapshots stay byte-identical.
- Agent templates are documents too: the shipped templates are a barebones Jonas and Sugary. A
  household's agent is a template plus what the household set. Owners change only an agent's name
  and custom instructions in this phase; everything else (model, servers, features, sections) comes
  from the template or, for the home household's own agents, from their seeded definitions.
- The agents in the agent's settings file are retired. jack, jonas, nabu and sugary are seeded into
  the home household's agents section by the migration. The eval keeps its own copies of today's
  definitions under the tests and drives those.

### Agents per household

- The agent builds a turn's agent from the turn's household's agents section, with the household's
  settings filled into the prompt (name, language, time zone) and its features deciding which shared
  servers and domain features the agent gets.
- Every model call made for a household uses its OpenRouter key: turns, workers, memory extraction,
  dreaming and its judge, skill preloading and the exec screen. Background work carries the household
  explicitly: an extraction request names it, and dreaming walks households and their people. The
  home household uses the deployment key. A household with no key refuses its turns with a message
  the person sees. The deployment's housekeeping calls stay on the deployment key.
- Custom agents become durable, kept per household and person in Redis.

### Shared servers

- A shared server reads its section of the calling household's configuration through one accessor
  over the verified call (beside the turn accessor), cached and invalidated as above: Idealista its
  keys; scheduling and Sugary the household's time zone; Telegram the household's bots.
- Timers become per household and per person: a timer records its owner and household. It rings on
  the household's satellites through its voice server (4a's relay) when its target is a satellite
  and the household has one; otherwise it rings as an insistent alert on the person's channels — the
  chat client (and its push notification) and Telegram — and dismissing it on any of them stops it
  on all, as an approval settles.

### Channels

- One shared Telegram bot, with the deployment's token, serves every household: a linked person's
  message goes to their household's default agent, and an `/agent` command switches the chat to
  another of the household's agents. The home household's jack and jonas bots are bots in its keys
  section, each bound to its agent. A group belongs to the household of the person who bound it.
- Spaces come from the household's spaces section: name, slug, colour and default agent. The chat
  client serves a household's spaces (manifest, icon, page), the hub admits only a space of the
  connection's household, and a new household starts with one default space. A space opens on its
  default agent unless the person picked another.

### Stores and metrics

- Every shared store's keys carry the household id: topics and their search documents, space
  indexes and push space subscriptions, schedules, custom agents, outposts, and metric roll-ups.
  Per-person stores stay keyed by user id.
- Metric events carry the household. Lemonade's use is counted per household as events: speech
  seconds transcribed, characters spoken, embeddings computed.

### The migration

- The household migration gains steps that seed the home household's configuration (settings from
  the deployment, agents from today's definitions, spaces from the chat client's list, the jack and
  jonas bots) and re-key every shared store into the home household. Like the existing steps they
  move only what is still unmoved, so a rerun changes nothing, and `--dry-run` writes nothing.

## Testing Decisions

A good test checks what a caller observes: which configuration a caller gets and whether its secrets
are readable, which agent a turn runs and whose key paid for it, which household's data a store
answers, where a timer rings. It never checks a document's layout beyond what the composed prompt
shows.

Seven seams:

1. **The accounts service's configuration API (existing surface).** Sections stored and migrated
   forward, secrets sealed and unsealed only to their own server, a save invalidating caches, the
   household taken from the verified call. Prior art: the internal-API tests.
2. **Prompt documents (existing).** The budget, staleness and snapshot tests run over the documents;
   snapshots unchanged. Prior art: `PromptBudgetTests`, `PromptSnapshotTests`, `PromptStalenessTests`.
3. **Agent building per household (existing).** With a fake model client: the household's agents,
   settings and key used for turns, workers, memory extraction and dreaming; no key refuses turns.
   Prior art: the `MultiAgentFactory`/`ThreadSession` tests.
4. **A real shared server host (existing).** Fetching its household's section: Idealista's keys,
   scheduling's time zone. Prior art: `McpSecretGateTests`.
5. **Channel servers (existing).** The shared Telegram bot routing to a household's default agent
   and `/agent`, a timer alert on a person's channels with dismiss everywhere, spaces per household in
   the hub. Prior art: `TelegramBotServiceTests`, the hub pipeline tests.
6. **The household migration (existing).** Against seeded stores: shared stores re-keyed, the home
   configuration seeded, a rerun changing nothing.
7. **The smoke tier (existing).** On a real stack, a reply from the home household's agent paid by
   its key and a household-scoped store.

## Out of Scope

- The owner's admin area, key probes, the operator's tenant list, suspend and usage views (4c).
- Editing prompt sections, skills, model, servers or features from a browser.
- Evaluating a household's own agents.
- Deleting or exporting a household.
- A household bringing its own Lemonade.

## Further Notes

- Phase 4b of the multi-tenant plan (`.scratch/multi-tenant/plan.md`, "Tenants decisions"), after
  4a (`.scratch/tenants-tunnel/`). It branches from `web-identity` and merges back into it.
- Deploying it needs the master key in `.env` for the accounts service and one run of the household
  migration's new steps.
