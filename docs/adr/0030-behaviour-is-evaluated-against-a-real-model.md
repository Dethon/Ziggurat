# 0030 — Behaviour is evaluated against a real model, over real servers, with fake externals

Status: accepted
Date: 2026-08-18

## Context

Most of what this agent is told to do lives in `Domain/Prompts/` as prose: which of three
mechanisms a reminder becomes, that a playlist is browsed before it is played, that a podcast
episode plays only by its exact uri, that an Obsidian wikilink is never "fixed" into Markdown.
Every one of those is a claim about what the agent will do, and none of them fails anything when
it stops being true. `PromptSnapshotTests`, `PromptBudgetTests` and `PromptStalenessTests` hold
the prompt's shape, size and vocabulary still; nothing holds its *effect*.

The obvious cheap answer is a scripted `IChatClient` that replays a canned tool-call sequence.
It is deterministic, free and instant, and it cannot fail when somebody deletes the two-step
decision procedure out of `TimerPrompt` — the test scripts the decision it then asserts. It
tests the plumbing, which is already tested.

## Decision

A behavioural scenario drives a **real model** through `McpAgent`, built from the **shipped
agent definition** in `Agent/appsettings.json`, against the **real MCP servers** hosted
in-process, with only the outermost externals faked: `FakeHaClient`, `FakeMusicAssistantServer`,
a stub voice hub, a temp-directory vault, a local static site for web browsing, a stubbed
subagent factory, and a Redis fixture.

Faking further in was rejected twice over. A fake `IFileSystemBackend` mounted straight into the
registry would mean the harness invents the tool schemas and prompt text the real server serves,
so a scenario could pass against a contract no deployment has. A synthetic `AgentSpec` would let
the suite stay green while the shipped model, provider routing, language or prompt sections are
misconfigured, which is one of the failures the suite exists to catch.

One fake sits deeper than that list, and knowingly: the **memory store**. Recall is injected as
data, but `memory_forget` needs something to search and delete from, and the real store's search
is a k-nearest query with no relevance floor — against a handful of seeded facts it returns all of
them, so every forget would empty the store and no scenario could say which fact the turn was
about. The eval's store matches lexically instead. The cost is stated where it lands: a scenario's
"nothing else was forgotten" assertion is true of that store and not of the deployment's, and the
unbounded delete is filed as a finding of its own rather than hidden by the substitution. What
these scenarios are evidence about is the agent's decision to forget, which is the subject the
spec keeps and retrieval quality is the subject it rules out.

The whole suite is opt-in under `Category=Eval` and never runs on a bare `dotnet test`, even
with an OpenRouter key present — unlike `Category=Llm`, which does. Sampling stays at provider
defaults: pinning temperature for the eval would add a knob production does not use, so green
would stop meaning "what ships works".

## Consequences

Every assertion reads one **recording** — the ordered tool invocations with arguments and
results, plus the final reply — captured by a decorator at the function-invocation seam, built
the way `ToolApprovalChatClient` already is. That seam is the only one that sees a call to a
tool that errored or does not exist, and it gives one order across mounts.

A real model makes runs stochastic, so a scenario declares **k of N** and the suite never
retries until green: half these assertions are negative ("nothing extra was called", "no other
entity changed"), and `LlmAttempt.UntilAsync`'s retry-until-positive would hide exactly the
regression being hunted. It also makes failures irreproducible by re-running, so a failed run
writes a self-contained dump — assembled system prompt, model and provider used, decorated turn,
every recorded call, final reply, failing assertion — and a full pass writes a per-claim
scorecard, which is what makes "it got worse after the model bump" a diff rather than a feeling.

The harness itself is deterministic code and is built test-first against a scripted
`IChatClient` — proving the permitted-set check fails on an extra call, that the partial-order
check fails when B precedes A, that the recorder captures a call whose tool threw. That is the
one place the scripted client belongs: testing the harness, never the prompts.
