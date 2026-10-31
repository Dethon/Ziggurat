---
paths:
  - "Domain/Prompts/**"
  - "Tests/Unit/Domain/Prompts/**"
  - "Tests/Snapshots/**"
  - "McpServer*/McpPrompts/**"
---

# The system prompt is a manifest, not a concatenation

Every section that can reach a system prompt is declared in `PromptManifest`, with a name, a
one-line purpose, a priority band, a token budget, an audience and a conflict policy — read from
the section's document (below), whose header is that declaration. `PromptComposer` turns a
`PromptContext` into sections and `PromptAssembly` orders, joins and audits them. Nothing decides an
order at a call site: adding a section is a document plus a line binding its text, never an
argument about where a `Prepend` goes.

- **A declaration is metadata; the words are bound to it.** Half these sections are written by
  somebody else — an MCP server serves its own prompt and can change it without this repo being
  rebuilt — so `PromptDeclaration.Bind(text)` is how text becomes a `PromptSection`. `ServedBy`
  names the compose service a section arrives from; null means the words are built here.
- **`PromptPriority` is the ordering, and later wins.** A section further down sits closer to the
  conversation, so it is the one the model applies. The date sits after every static section
  because the provider's cache keys on a byte prefix; custom instructions and the channel override
  sit after that; the language rule is last.
- **A prompt name is a key.** Two servers answering to `system_prompt` cannot both be budgeted, so
  each server's `[McpServerPrompt(Name = …)]` reads the `Name` constant of its section's class in
  `Domain/Prompts`, and the section's document is named after that same constant. The constant
  stays in code because an attribute takes nothing else.
- **A contradiction is a question with an answer.** A section names the rules it governs
  (`PromptRules`) and the sections it beats. Two sections claiming one rule with no declared winner
  is reported by the assembly and fails the tests; so is a section claiming to override one that is
  read after it, because that claim is simply false.
- **Behaviour belongs in a section, not in `customInstructions`.** Prose in `appsettings.json` is
  unreviewable in a diff, unreachable from a test and impossible to budget. An agent names sections
  instead (`promptSections`), resolved against `PromptManifest.SelectableSections` — validated for
  the whole deployment at startup and again per agent as its spec is projected. `VoicePrompt` is
  the worked example: it was three kilobytes of JSON string.
- **A served section reaches every agent that dials its server, unless it names an audience.**
  `PromptAudience.ForAgents(...)` is filtered in `PromptAssembly.Build`: `sugary_prompt` is the
  `sugary` agent's, though Jonas dials `mcp-sugary` too for her log. `PromptAudienceTests` checks an
  audience names a configured agent.
- **An agent may dial its servers for their prompts alone.** `noTools` on a definition: the session
  offers no tool, no skill and no mount, so no `load_skill`, no `skills` section and no preload is
  asked (`sugary`, a one-answer estimator). It enables no features and no outposts, or the
  projection refuses it.
- **Over budget is reported, never thrown.** `McpAgent` logs the assembly's warnings once per
  distinct set — a prompt that grew is a worse outcome than a turn that fails, and a server that
  grows its prompt must not take the agent down. What fails a build is `PromptBudgetTests`.

## A section is a document

Every section's words and declaration live in one text document, `Domain/Prompts/Sections/<name>.md`,
so the prompt is data a later editor can work on and a claim sits beside its prose (ADR 0031). The
classes beside them (`TimerPrompt`, `VoicePrompt`, …) are handles: the `Name` constant, the
server's `Description`, the C# accessors the hosts call, and one `PromptClaim` per claim a scenario
cites, resolved from the document by id (`document.Claim("timers.…")`) — a statement lives in the
document only, and a handle whose id the document stopped declaring fails the first test that
touches its class.

- **The format.** A header between two `---` lines, then the words. A header line is
  `key: value`, split at the first colon (a purpose is a sentence), and `#` starts a comment line —
  the reason a budget is what it is goes there. A section's keys: `name` (= the file name), `purpose`,
  `priority` (a `PromptPriority` band by name), `budget`, `served-by`, `audience-agents`,
  `audience-channels`, `governs` (`PromptRules` words), `beats`, `selectable`, `placeholders`, and
  `claims:` followed by `- <id>: <statement>` lines. An unknown key, band or rule fails the document
  (`PromptDocument.AsSection`). After the main body, `<!-- part: <name> -->` lines open named parts:
  words the host adds or swaps in — the timer roster's heading, the language pins, the mounts
  section's machines and shell paragraphs. Each body is its lines without the blank lines around
  them; CRLF is read as LF.
- **A placeholder is `{{name}}`**, lowercase, digits and `_`, no spaces — so Jinja's and Templater's
  `{{ … }}` in a skill body stay prose. It is filled verbatim, in one pass. The host fills what only
  it knows: the date, the agent's name and description, the user, the custom instructions, the
  language, the satellites, the printer's formats, the sandbox's mount point and workspace, the
  mounts, the scheduling setup. A placeholder absorbs its own separator where the words vary — the
  identity's ` {{description}}` is empty for an agent with none, `{{satellites}}` and `{{setup}}`
  are where a block lands with its blank line — so no conditional lives in a document; the host
  chooses a part, never an `if`. Lists and their item formats (a mount line, a roster line, an
  agent line) stay in code; every sentence is in a document.
- **The contract is held from both ends.** The header's `placeholders:` lists every hole the bodies
  use, and reading the document fails on one declared and used nowhere or used and undeclared —
  an edit that drops `{{date}}` from the words fails where the documents are loaded, at startup and
  in `PromptDocumentsTests`. The host names what it fills when it takes a body
  (`document.Template("satellites")`, `document.Part("satellites", "roster")`, in a static field), so
  a hole dropped from the header and the words alike fails the host's class; `PromptTemplate.Fill`
  refuses a value missing or extra. A section with nothing to fill is read with `document.Text`.
- **Shipped embedded, in Domain.** One folder because Domain owns every declaration — the agent,
  the eval and every server read the manifest, and the servers already take their words from
  `Domain/Prompts`. Embedded rather than copied to output, for the public suffix list's reason: the
  words are the code's, not a policy a host may override, and a container that edited its own
  prompt would make every snapshot a statement about another prompt. The repository's
  `.dockerignore` drops `**/*.md` and puts `Domain/Prompts/**/*.md` back
  (`DeploymentConformanceTests` holds it), and `Domain.csproj` fails a build that embedded no
  section or no skill. Agent templates join `Domain/Prompts/` as documents of their own kind.
- **A skill is a document too**, `Domain/Prompts/Skills/<name>.md`, in the same format and read by
  `PromptDocument.AsSkill`. Its keys: `name` (= the file name), `description` (the advertisement,
  verbatim — Jev and the model read it), `description-budget`, `body-budget`, `served-by` (required),
  `preload-reads`, `placeholders` and `claims:`, the trigger claim first. It lives in Domain and not
  beside its server, because the server ships the skill (ADR 0039) but the agent, the eval, Jev's
  preload and every budget test read its declaration from the manifest, and none of them references
  a server; each server references Domain, so the document is in its image already. The classes
  beside them (`HomeAssistantSkill`, `SandboxSkill`, …) are handles: the `Name` constant, the
  `Text` or `For(...)` the server's `AddSkills` takes, and the claim handles. The manifest lists
  skills by name; the order a model sees them in is its servers' registration order.
- **The changed tier reads the header.** `ChangeScope` parses a changed `.md` with the same parser,
  so an edit anywhere in a document — words, a part, a statement — selects every scenario testing a
  claim its header declares, and an id its words merely mention is not a declaration. Code still
  declares by spelling a quoted id, which is how a claim handle's file, or a skill still declared in
  code, selects. `ChangeScopeTests` runs every shipped document against the real suite.
- **What is not a section stays code.** The memory pipeline's extraction, consolidation and profile
  prompts are the system prompts of its own model calls, and the HA setup index is a file the mount
  builds on every read; neither reaches an agent's prompt as a section.

## A skill is a section the model reads on demand

The glossary (`CONTEXT.md` § Prompt) pins four words. The **base prompt** is every section an
agent reads on every turn. A **skill** is a body the model loads with one `load_skill` call when a
request calls for it; only its name and one-line description stand in the base prompt. A **trigger
claim** is what that description asserts: a request of a named kind gets it loaded. A **preload**
is the host loading first: before the model's first call, `SkillsProvider` asks Jev (`ISkillPreloader`,
`Domain/Skills/`) which skills the request needs, judged on the same descriptions, and a confident
answer puts the body into the conversation — after the user message, persisted with the turn,
never twice. ADR 0039 (refined 2026-09-18) is the decision; these are the rules the next section
lands on. A turn addressed to the Lemonade chat host asks
nothing, and neither does a worker it spawns. The Jev client holds that rule
(`.claude/rules/mcp-hosting.md`); the group and the provider only name the model on
`SkillPreloadRequest.Caller` (`JudgmentCaller.TurnModel`) — from the request's own patch, which for a worker is the
parent turn's, written onto its request by the run tool from the `ConversationContext` it also carries.

- **A skill may already be in the conversation when a turn starts, and one that is, is loaded.**
  The `skills` section says so to the model; `SkillLoadTool.LoadedIn` says so to the preloader,
  reading every load off the history whoever made it. There is no second record of what is loaded.
- **A preload is recorded as a load and sent as a note.** The thread keeps the pair a `load_skill`
  call leaves (`SkillLoadTool.AsPreloaded`, its call ids marked as the host's), which is what
  `LoadedIn`, the truncator and every reader of a thread see. The model is never sent that pair:
  `OpenRouterChatClient` writes it, last and on the copy that goes out, as one user-role message
  after the request (`SkillPreloadNote`) — "[Loaded for this request before your first call, by
  the host: …]" and the wrapped body, then a part per read. A call the model did not make is an
  assistant turn it never took, and each model and provider made something different of one:
  Anthropic's were handed it with the host's reasoning dropped and carried on mid-turn (Haiku 5.5
  opened slow spoken work with its word on 0 of 10, gpt-6-luna on 4; every model on 10 of 10 with
  the note), DeepSeek's own host refused it without reasoning to take back, Mistral cut its call
  ids to nine characters. So there is no host-made reasoning and no rule about ids any more, and
  nothing per model. A `developer` or `system` item is not an alternative — OpenRouter hoists it
  to the head of the conversation for chat-completions hosts, which moves the cached prefix —
  and neither is the pair placed before the request. Pairs stored by earlier builds are sent as
  the same note and never migrated. The note's wording was measured as written; changing it is a
  new measurement on every model, not an edit.
- **A preload makes the reads the body's first line orders.** `SkillDeclaration.PreloadReads`
  names the files a body says to read in the same turn as the skill (the home skill: the setup
  index), and a preload reads them through the session's own `file_read` and records the pair
  beside the load — sent in the same note, under its own line — so the model's first call after a
  head start is the action. A read that fails
  is left out and the body tells the model to read. The event names the reads; the eval counts
  each as a preloaded `file_read`, so the read every home scenario requires is met by either
  reader.
- **The description has two readers and one text.** Jev judges by `PromptSkill.Description`
  verbatim — never a second criteria text — so a red trigger claim has one fix, in the description,
  and a description that works on only one reader shows as a lopsided loader column on the eval
  scorecard. Overlaps between descriptions are fixed in the descriptions.

- **Timing decides what moves, never subject.** A rule the model needs *before* it decides what to
  do — which mechanism, which tool, which mount — stays in the base prompt. A rule it needs only
  *while* doing the thing — a file's layout, an action's arguments, how a result is read — moves into
  a skill. A section with both splits into a standing stub carrying the choosing rules and their
  claims, plus a body; the stub names the skill in one sentence.
- **The size floor is about 500 tokens.** Below it the advertisement and the load hop cost more than
  the body sheds, so the section stays whole. Persona, identity, delegation, memory, the mounts
  list, voice, language, printing and Idealista stay for that reason or because they are read before
  any choice.
- **A skill is a manifest entry.** `PromptManifest.Skills` declares each one beside the sections:
  name, the description that is the advertisement, a description budget, a body budget, the server
  that serves it and its claims — read from its document's header, its text from the same
  document's words, and the server publishes it through the hosting helper — never a hand-written
  resource.
- **The ceiling ratchets.** `PromptManifest.StandingTokens` is the largest agent's declared sections
  rounded up to the hundred, and the budget tests refuse a figure left where it was. A move lowers
  it by editing that one number.
- **Red for a skill claim is demonstrated with the body's prose deleted and the description
  intact.** That reddens the body claim. A red with the description deleted is a missing load — the
  trigger claim's red, cited by every scenario of the family — and says nothing about the body. The
  description deleted blinds both readers at once, Jev and the model, so the demonstration still
  holds now that a trigger claim reads "a request of this kind gets the skill loaded" rather than
  naming who loads it; the scorecard's `loader` column says who did. The commit that cites a skill
  claim notes which demonstration it made.

## The tests are the point

- `Tests/Snapshots/prompt.*.txt` hold each agent's whole assembled prompt, with a per-section
  token/budget header. Regenerate with `UPDATE_PROMPT_SNAPSHOTS=1 dotnet test` and **read the
  diff** — a prompt regression is otherwise the least diagnosable change there is.
- `PromptStalenessTests` walks the prompts against the code they describe: every prompt a server
  serves is declared, every declaration matches a prompt some server still serves, every service an
  agent dials has its prompts declared, no section names an `fs_`-prefixed tool, and every path a
  section teaches starts at a mount that exists. It caught `/Movies/Action/` in the library prompt,
  where the real path is `/media/Movies/Action/`. Skills are walked the same way, in both
  directions, and a skill body's paths are checked like a section's.
- `Tests/Snapshots/skill.*.md` hold each skill's body under its budget, so a body that grows is a
  diff somebody reads; the agent snapshots show the advertised list exactly as the model sees it.
- `VoiceOverridesFormattingTests` proves the spoken rules beat the screen-oriented ones for the
  agent that actually speaks, and ties the section's declared channel to the routing default that
  sends that channel's messages to it.
