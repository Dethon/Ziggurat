---
paths:
  - "Domain/Prompts/**"
  - "Tests/Unit/Domain/Prompts/**"
  - "Tests/Snapshots/**"
  - "McpServer*/McpPrompts/**"
---

# The system prompt is a manifest, not a concatenation

Every section that can reach a system prompt is declared in `PromptManifest`, with a name, a
one-line purpose, a priority band, a token budget, an audience and a conflict policy.
`PromptComposer` turns a `PromptContext` into sections and `PromptAssembly` orders, joins and
audits them. Nothing decides an order at a call site: adding a section is a declaration plus a line
binding its text, never an argument about where a `Prepend` goes.

- **A declaration is metadata; the words are bound to it.** Half these sections are written by
  somebody else — an MCP server serves its own prompt and can change it without this repo being
  rebuilt — so `PromptDeclaration.Bind(text)` is how text becomes a `PromptSection`. `ServedBy`
  names the compose service a section arrives from; null means the words are built here.
- **`PromptPriority` is the ordering, and later wins.** A section further down sits closer to the
  conversation, so it is the one the model applies. The date sits after every static section
  because the provider's cache keys on a byte prefix; custom instructions and the channel override
  sit after that; the language rule is last.
- **A prompt name is a key.** Two servers answering to `system_prompt` cannot both be budgeted, so
  each server's `[McpServerPrompt(Name = …)]` reads the `Name` constant beside its text in
  `Domain/Prompts` and the manifest declares that same constant.
- **A contradiction is a question with an answer.** A section names the rules it governs
  (`PromptRules`) and the sections it beats. Two sections claiming one rule with no declared winner
  is reported by the assembly and fails the tests; so is a section claiming to override one that is
  read after it, because that claim is simply false.
- **Behaviour belongs in a section, not in `customInstructions`.** Prose in `appsettings.json` is
  unreviewable in a diff, unreachable from a test and impossible to budget. An agent names sections
  instead (`promptSections`), resolved against `PromptManifest.SelectableSections` — validated for
  the whole deployment at startup and again per agent as its spec is projected. `VoicePrompt` is
  the worked example: it was three kilobytes of JSON string.
- **Over budget is reported, never thrown.** `McpAgent` logs the assembly's warnings once per
  distinct set — a prompt that grew is a worse outcome than a turn that fails, and a server that
  grows its prompt must not take the agent down. What fails a build is `PromptBudgetTests`.

## A skill is a section the model reads on demand

The glossary (`CONTEXT.md` § Prompt) pins three words. The **base prompt** is every section an
agent reads on every turn. A **skill** is a body the model loads with one `load_skill` call when a
request calls for it; only its name and one-line description stand in the base prompt. A **trigger
claim** is what that description asserts: a request of a named kind loads it. ADR 0039 is the
decision; these are the rules the next section lands on.

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
  that serves it and its claims. Its text lives in `Domain/Prompts` beside its name constant, like a
  section's, and the server publishes it through the hosting helper — never a hand-written resource.
- **The ceiling ratchets.** `PromptManifest.StandingTokens` is the largest agent's declared sections
  rounded up to the hundred, and the budget tests refuse a figure left where it was. A move lowers
  it by editing that one number.
- **Red for a skill claim is demonstrated with the body's prose deleted and the description
  intact.** That reddens the body claim. A red with the description deleted is a missing load — the
  trigger claim's red, cited by every scenario of the family — and says nothing about the body. The
  commit that cites a skill claim notes which demonstration it made.

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
