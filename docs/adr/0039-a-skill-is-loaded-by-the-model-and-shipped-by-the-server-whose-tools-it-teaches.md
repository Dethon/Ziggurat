# 0039 — A skill is loaded by the model and shipped by the server whose tools it teaches

Status: accepted
Date: 2026-09-08

## Context

Every agent's instructions are assembled on every turn from the sections its servers and its
definition declare: nabu reads about 14.3k tokens of a 20k ceiling before it sees the request,
jonas about 13.5k. The Home Assistant guide alone is 5.2k, and it is the one section that grows
with the deployment rather than with an edit. Nothing in the prompt is deferred: every server's
prompt is fetched whole at warmup and inlined, and the manifest's audiences exist to keep the
assembled bytes identical across a conversation, because the provider's cache is a byte prefix.

Most of that text is *doing* knowledge — how a vault note is laid out, what the sandbox refuses,
how to read a history file, how to phrase a watch — that a turn about the weather pays for and
never uses. The framework the agent already runs on (`Microsoft.Agents.AI` 1.20.0) ships a
stable skills provider: names and descriptions go into the instructions, and the model fetches a
body with a tool call when a request calls for it. The question was how much of the prompt
should move behind that call, who owns what moves, and what it does to the behavioural eval
(ADR 0030, ADR 0031), which is the only instrument that can say whether the move cost anything.

Two prior facts bounded the answer. Guidance was once moved into a tool description
(delegation, 2026-08-20) and measured as a coin flip either way, so on-demand teaching is not
free of behavioural risk and has to be measured. And the setup summary's own compaction note
records the trade this is made of: a round trip costs about 1.15 s, prompt prefill about
0.05 ms per token, so buying a turn back with tokens only pays while the tokens stay cheap.

## Decision

**What moves is decided by timing, not by subject.** A rule the model needs *before* it decides
what to do stays in the base prompt; a rule it needs only *while* doing the thing becomes a
skill. "A duration under four hours is a timer, never a calendar alarm" steers the choice between
two tools and stays; "how to read a history file" is needed after the choice and moves. A section
that has both splits into a standing stub and a skill body; a section under about 500 tokens
stays whole, because the advertisement plus the load hop costs more than it sheds.

**The model triggers a load; the host never does.** The advertised description is the whole
trigger. A skill nobody loads is a red description, not a red body, and the eval can tell the two
apart only if the host is not quietly loading on its behalf. Voice pays the extra hop like every
other channel; if a satellite shows it hurting, the fallback is a bounded pre-load and it is a
one-line policy, but it is not the design.

**The server whose tools a skill teaches ships it.** Each tool server publishes its skills as
resources in the standard shape — an index and one body per skill — read at warmup by the same
client manager that reads its prompt, and handed to the framework's stable provider as inline
skills. The agent definition grants which servers, and so which skills, exactly as it grants
tools; a subagent gets skills with the servers it is allowed; an outpost may publish a skill
under the same undeclared-name budget it already gets for a prompt. A deployment that omits a
server cannot advertise guidance for tools it does not have.

**A skill is a manifest entry.** It is declared in the one table beside the standing sections,
with a description budget, a body budget, the server that serves it, its own snapshot and its
claims. The advertisement's prose is itself a declared section, and the framework's template is
cut down to the bare list it appends; the prompt fixture renders through the provider, so the
snapshot shows what the model sees. The base prompt's ceiling ratchets down after each section
moves, so what left cannot grow back.

**A skill's description is a claim.** It asserts that a request of a named kind loads it, is
declared beside the description and is cited by every scenario of that family. A load is an
ordinary tool call in the permitted set: loading the wrong skill is a wrong choice and reddens.
Red for a claim in a body is demonstrated with the body's prose deleted and the description
intact; a red with the description deleted is a missing load, which is the trigger claim's red,
not the body's.

**A body is static, and the live part of the home moved out of it.** A loaded body stays in the
conversation for its life and is never reloaded; a conversation that spans a deploy keeps the
body it loaded. What must be fresh is not prose: the setup index becomes a file the mount serves,
built on read, and the standing stub says to load the skill and read the file in one turn. It
is not a recursive listing, because the index says in 10.7k characters what the two trees say in
several times that, and carries the satellite rooms and the action table by class, which are
not in the tree.

**Rollout is one section at a time, largest first**, each gated by a full-tier eval pass before
and after: Home Assistant (guide, with watches as a second skill), vault, web browsing,
scheduling, sandbox, timers. Equal behaviour with fewer standing tokens and better authoring is
worth merging; a regression is not.

## Considered options

- **Host pre-loads by channel or on first tool call.** Rejected: a passing scenario could no
  longer say whether the model chose the skill or the host forced it, and the description would
  never be tested.
- **The framework's MCP skills source.** Alpha only at the shipped version; rejected for now in
  favour of the standard resource shape and a reader in the repo. Swapping in the framework's
  source later is a one-file change because the wire shape is the same.
- **Skills as MCP prompts.** The prompt fetch already exists; rejected because nothing about the
  wire shape would be standard and a later swap would rewrite every server.
- **The setup index inside the skill body.** One load, but stale for the life of a conversation
  that today sees an index at most a minute old. Reload-on-change was considered and set aside
  as a mechanism the file makes unnecessary.
- **Move by subject or by tool coupling.** Both put "which tool" rules behind a load the model
  has not yet decided to make.

## Consequences

Servers publish resources for the first time, and the client manager reads two things from each
server instead of one. `BasePrompt.cs` is renamed to match its section, `core_directive`, so
"base prompt" can name the standing whole without the file contradicting it. `CONTEXT.md` pins
**base prompt**, **skill**, **trigger claim** and **setup index**. Every skill costs a declared
entry, a description that is a claim, a snapshot and a body budget — the same friction ADR 0031
bought for sections, extended to what is no longer always read.
