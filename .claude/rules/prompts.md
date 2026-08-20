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

## The tests are the point

- `Tests/Snapshots/prompt.*.txt` hold each agent's whole assembled prompt, with a per-section
  token/budget header. Regenerate with `UPDATE_PROMPT_SNAPSHOTS=1 dotnet test` and **read the
  diff** — a prompt regression is otherwise the least diagnosable change there is.
- `PromptStalenessTests` walks the prompts against the code they describe: every prompt a server
  serves is declared, every declaration matches a prompt some server still serves, every service an
  agent dials has its prompts declared, no section names an `fs_`-prefixed tool, and every path a
  section teaches starts at a mount that exists. It caught `/Movies/Action/` in the library prompt,
  where the real path is `/media/Movies/Action/`.
- `VoiceOverridesFormattingTests` proves the spoken rules beat the screen-oriented ones for the
  agent that actually speaks, and ties the section's declared channel to the routing default that
  sends that channel's messages to it.
