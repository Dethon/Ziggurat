---
paths:
  - "Domain/Prompts/**"
  - "Tests/Unit/Domain/Prompts/**"
  - "Tests/Snapshots/**"
  - "McpServer*/McpPrompts/**"
---

# The system prompt is a manifest, not a concatenation

Every section that can reach a system prompt is declared in `PromptManifest`, read from its document's
header. `PromptComposer` turns a `PromptContext` into sections; `PromptAssembly` orders, joins and
audits them. Nothing decides an order at a call site: a section is a document plus a line binding its
text, never an argument about where a `Prepend` goes.

- **A declaration is metadata; the words are bound to it** (`PromptDeclaration.Bind(text)`), because a
  server serves its own prompt and can change it without this repo being rebuilt. `ServedBy` names the
  compose service it arrives from; null means built here.
- **`PromptPriority` is the ordering, and later wins** (closer to the conversation). The date sits after
  every static section (the provider's cache keys on a byte prefix); custom instructions and the channel
  override after it; the language rule last.
- **A prompt name is a key** (two servers answering to `system_prompt` cannot both be budgeted). A
  server's `[McpServerPrompt(Name = …)]` reads the `Name` constant of its section's class in
  `Domain/Prompts`, and the document is named after that same constant; the constant stays in code
  because an attribute takes nothing else.
- **A contradiction is a question with an answer.** A section names the rules it governs (`PromptRules`)
  and the sections it beats. Two claiming one rule with no declared winner, or one claiming to override
  a section read after it, is reported by the assembly and fails the tests.
- **Behaviour belongs in a section, not in `customInstructions`** (`appsettings.json` prose cannot be
  reviewed, tested or budgeted). An agent names sections (`promptSections`), resolved against
  `PromptManifest.SelectableSections`, validated at startup and per agent as its spec is projected.
- **A served section reaches every agent that dials its server, unless it names an audience**
  (`PromptAudience.ForAgents`, filtered in `PromptAssembly.Build`): `sugary_prompt` is the `sugary`
  agent's though Jonas dials `mcp-sugary` too; `library_prompt` is `jack`'s, and the templates' agents
  dialling the library (`nabu`, `jonas`) get `library_guide`. An audience naming an id also takes a household's numbered ids of
  that template (`nabu-2`). `PromptAudienceTests` checks an audience names a configured agent.
- **`noTools`**: an agent dials its servers for their prompts alone — no tool, skill, mount, `load_skill`,
  `skills` section or preload — and enables no features or outposts, or the projection refuses it.
- **Over budget is reported, never thrown**: `McpAgent` logs the warnings once per distinct set, so a
  server growing its prompt cannot take the agent down. `PromptBudgetTests` fails the build.

## A section is a document

Words and declaration live in `Domain/Prompts/Sections/<name>.md` (ADR 0031; format in
`PromptDocument`'s comment). The classes beside them (`TimerPrompt`, `VoicePrompt`, …) are handles: the
`Name` constant, the server's `Description`, host accessors, and one `PromptClaim` per cited claim,
resolved by id (`document.Claim("timers.…")`). A statement lives in the document only; a handle whose id
the document stopped declaring fails the first test touching its class.

- **The format.** Header between two `---` lines, `key: value`, `#` comment lines (a budget's reason
  goes there). Section keys: `name` (= file name), `purpose`, `priority` (a `PromptPriority` band),
  `budget`, `served-by`, `audience-agents`, `audience-channels`, `governs` (`PromptRules` words),
  `beats`, `selectable`, `placeholders`, `claims:` with `- <id>: <statement>` lines. An unknown key, band
  or rule fails the document (`PromptDocument.AsSection`). `<!-- part: <name> -->` opens a named part the
  host adds or swaps in. Bodies drop surrounding blank lines; CRLF reads as LF.
- **A placeholder is `{{name}}`** — lowercase, digits, `_`, no spaces, so `{{ … }}` in a skill body stays
  prose — filled verbatim in one pass. It absorbs its own separator (the identity's ` {{description}}`),
  so no conditional lives in a document: the host chooses a part, never an `if`. Lists and item formats
  stay in code; every sentence is in a document.
- **The contract is held from both ends.** `placeholders:` lists every hole the bodies use; declared and
  unused, or used and undeclared, fails at startup and in `PromptDocumentsTests`. The host names what it
  fills when it takes a body (`document.Template(...)`, `document.Part(...)`, in a static field), so a
  hole dropped from header and words alike still fails the host's class; `PromptTemplate.Fill` refuses
  a value missing or extra. Nothing to fill: `document.Text`.
- **Shipped embedded, in Domain** — the words are the code's, not a policy a host may override. The
  `.dockerignore` drops `**/*.md` and puts `Domain/Prompts/**/*.md` back (`DeploymentConformanceTests`);
  `Domain.csproj` fails a build that embedded no section or no skill.
- **A skill is a document too**, `Domain/Prompts/Skills/<name>.md` (`PromptDocument.AsSkill`). Keys:
  `name`, `description` (the advertisement, verbatim — Jev and the model read it), `description-budget`,
  `body-budget`, `served-by` (required), `preload-reads`, `placeholders`, `claims:` with the trigger
  claim first. It lives in Domain, not beside its server (ADR 0039): the agent, eval, preload and budget
  tests read it and none references a server. Its handle (`HomeAssistantSkill`, …) holds `Name`, the
  `Text` or `For(...)` the server's `AddSkills` takes, and claim handles; it is a `PromptManifest.Skills`
  entry, and a server publishes it through the hosting helper, never a hand-written resource. A model sees skills in server registration order.
- **The changed tier reads the header.** `ChangeScope` parses a changed `.md` with the same parser: any
  edit selects every scenario testing a claim its header declares; an id the words merely mention is not
  a declaration. Code declares by spelling a quoted id. `ChangeScopeTests` runs every shipped document.
- **What is not a section stays code**: the memory pipeline's own model-call prompts and the HA setup
  index.

## An agent template is a document

`Domain/Prompts/Agents/<name>.md` (CONTEXT.md, "Agent template"), embedded like sections, read by
`AgentTemplates` into an `AgentDefinition` whose id is the file name. The header is the definition, its
keys `name`, `purpose`, `agent-name` (the name a household starts with), `model`, `reasoning-effort`,
`provider-routing` (one JSON object on its line, a threshold an object as a seeded definition's is),
`servers`, `features`, `whitelist`, `prompt-sections` (comma lists), `uses-outposts`, `no-tools`,
`shared-routing-session-minutes`; an unknown key fails the document; the words are the description the identity section reads
after the agent's name; no placeholders, parts or claims. Today: `jonas` (general), `sugary` (the
estimator) and `nabu` (voice: an owner adds it and no household starts with it; it answers the
household's satellites unless the owner marks another agent to, and speaks the household's language,
never a pinned one). What each template dials is its own header comment, and its own servers reach a turn only where the household set them up
(`.claude/rules/household-config.md`, "The agents a household runs"). A household gives a template its
id, name and custom instructions; everything else is the template's. The prompt snapshots cover the
home's seeded agents (the eval's copies) and the voice template (`prompt.template.nabu.txt`,
`AgentPromptFixture`'s `template.` ids) — otherwise a household's spoken agent's prompt is nowhere a
reviewer reads it.

## A skill is a section the model reads on demand

Terms in `CONTEXT.md` § Prompt; ADR 0039 (refined 2026-09-18). The **base prompt** is read every turn;
a **skill** body is loaded by one `load_skill` call, only its name and description in the base prompt;
its **trigger claim** is that a request of a named kind gets it loaded. A **preload**: before the
model's first call `SkillsProvider` asks Jev (`ISkillPreloader`, `Domain/Skills/`) on the same
descriptions, and a confident answer puts the body in the conversation, recorded after the user message,
never twice. A turn to the Lemonade chat host, and its workers, ask nothing (the Jev client holds it,
`.claude/rules/mcp-hosting.md`); the group and provider only name the model on
`SkillPreloadRequest.Caller` (`JudgmentCaller.TurnModel`) from the request's own patch — a worker's is
the parent's, written onto its request by the run tool from the `ConversationContext` it carries.

- **A skill already in the conversation is loaded.** The `skills` section tells the model;
  `SkillLoadTool.LoadedIn` tells the preloader, reading every load off the history whoever made it. No
  second record exists.
- **A preload is recorded as a load and sent as a note.** The thread keeps the `load_skill` pair
  (`SkillLoadTool.AsPreloaded`, call ids marked the host's), which `LoadedIn`, the truncator and every
  reader see. The model never gets the pair: `OpenRouterChatClient` writes it, last, on the outgoing
  copy (the note is never stored), as one user-role message **ahead of** the request (`SkillPreloadNote`) so the provider caches it
  — no host-made reasoning, no id rules, nothing per model (a call the model never made is an assistant
  turn it never took, and every provider made something different of one; why in `SkillPreloadNote`). Not a `developer`/`system` item (OpenRouter
  hoists it, moving the cached prefix). Earlier builds' stored pairs are sent as the same note, never
  migrated. The wording was measured as written: changing it is a new measurement on every model.
- **A preload makes the reads the body's first line orders** (`SkillDeclaration.PreloadReads`; the home
  skill's setup index), through the session's own `file_read`, recorded beside the load and sent in the
  same note, so the model's first call is the action. A failed read is left out (the body says to read).
  The eval counts each as a preloaded `file_read`.
- **The description has two readers and one text.** Jev judges `PromptSkill.Description` verbatim, never
  a second criteria text, so a red trigger claim — and any overlap — is fixed in the description.
- **Timing decides what moves, never subject.** A rule needed *before* choosing (mechanism, tool, mount)
  stays in the base prompt; one needed *while* doing it (layout, arguments, reading a result) moves to a
  skill. A section with both splits into a standing stub with the choosing rules, claims and one sentence
  naming the skill, plus a body.
- **The size floor is about 500 tokens**; below it the advertisement and hop cost more than they shed.
  Persona, identity, delegation, memory, the mounts list, voice, language, printing and Idealista stay.
- **The ceiling ratchets.** `PromptManifest.StandingTokens` is the largest agent's declared sections
  rounded up to the hundred; the budget tests refuse a figure left where it was after a move.
- **Red for a skill claim is demonstrated with the body's prose deleted and the description intact.**
  Deleting the description reddens the trigger claim (both readers blinded) and says nothing about the
  body; the scorecard's `loader` column says who loaded. The commit citing a skill claim notes which
  demonstration it made.

## The tests are the point

- `Tests/Snapshots/prompt.*.txt`: each agent's assembled prompt with per-section token/budget headers;
  `Tests/Snapshots/skill.*.md`: each skill body. Regenerate with `UPDATE_PROMPT_SNAPSHOTS=1 dotnet test`
  and **read the diff**.
- `PromptStalenessTests` walks prompts and skills against the code both ways: every served prompt
  declared and every declaration served, every dialled service's prompts declared, no `fs_`-prefixed
  tool named, every taught path starting at a real mount.
- `VoiceOverridesFormattingTests`: spoken rules beat screen rules for the speaking agent. A channel's
  sections reach whichever agent its conversation group is built for (`PromptManifest.ForChannel`), so
  the voice section is the satellite's, not one agent's.
