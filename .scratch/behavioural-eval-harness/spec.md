# Behavioural evaluation harness

Status: ready-for-agent

## Problem Statement

What the assistant is supposed to do is written down more thoroughly than it is verified. The
operational contracts live as prose in the prompt sections — a duration under four hours is a
timer and never a calendar alarm, a playlist is browsed before it is played, a podcast episode
plays only by its exact uri, an Obsidian wikilink is never "fixed" into Markdown, a voice reply
is one sentence that still repeats the number the user said.

Nothing fails when any of that stops being true. The existing prompt tests hold the prompt's
shape, its size and the names it uses; none of them observes what the agent does when it reads
it. So a prompt edit, a model upgrade, a tool rename or a changed tool description can quietly
change behaviour, and the first report is a person noticing the assistant set the wrong kind of
reminder. Diagnosing that after the fact means re-reading a large system prompt and guessing.

## Solution

A suite of **scenarios**: one user request each, run against a real model with the deployment's
own agent definition, against the real MCP servers with only the outermost externals faked. Each
scenario declares what must hold afterwards — which tools were called with which arguments and
paths, what order they were in, that nothing extra was called, that the state change actually
happened, and that the reply obeyed the channel's format.

The prose keeps teaching the model, and each falsifiable statement it makes becomes a declared
**claim** that scenarios cite, so an untested rule is visible rather than assumed. A run produces
a **recording** that every assertion reads, a failure writes a dump that explains itself without
a re-run, and a full pass writes a per-claim scorecard, so "behaviour got worse after the model
bump" becomes a diff.

## User Stories

1. As a prompt author, I want a scenario to fail when I delete a rule from a prompt section, so that I learn the rule was load-bearing before the change ships.
2. As a prompt author, I want to know which of my rules nothing tests, so that I can tell an assumption from a verified contract.
3. As a prompt author, I want to reword a rule for clarity and see the scenarios stay green, so that I can improve wording without fearing a silent behavioural change.
4. As a prompt author, I want a cheap tier I can run while editing a single prompt, so that a quick check does not cost a full suite.
5. As a maintainer upgrading the model, I want a per-claim pass rate before and after, so that a regression is a diff rather than an impression.
6. As a maintainer, I want the suite to run against the shipped agent definition, so that a misconfigured model, provider routing, language or prompt section fails the suite instead of passing it.
7. As a maintainer, I want the suite never to run on a bare test invocation, so that a routine test run does not spend money without being asked.
8. As a maintainer, I want a failed run to write everything needed to understand it, so that I do not have to reproduce a stochastic failure to diagnose it.
9. As a maintainer, I want to know which model and provider actually served a failed run, so that I can tell a routing surprise from a prompt defect.
10. As a maintainer, I want to run one scenario against a different model, so that I can evaluate a candidate before switching the deployment to it.
11. As a voice user, I want a reminder in twenty minutes to become a countdown timer, so that it rings where I am rather than landing in a calendar.
12. As a voice user, I want "turn the air conditioning off in an hour" to become a scheduled action, so that something actually happens rather than a voice telling me to do it.
13. As a voice user, I want "wake me at seven" to land on the alarms calendar, so that it survives a restart and can escalate.
14. As a voice user, I want a timer I ask for in the kitchen to ring in the kitchen, so that I hear it.
15. As a chat user, I want the assistant to ask which room a timer should ring in, so that it does not guess a target when there is no speaking room.
16. As a user, I want a request to turn one light off to change exactly that one thing, so that I do not find another light, a scene or a media player changed too.
17. As a user, I want a state change I asked for to have actually completed, so that a confident reply is not covering a call that failed.
18. As a user, I want the assistant to look my playlists up before playing one, so that it plays the list I have rather than one it invented a name for.
19. As a user, I want a podcast episode played by its exact uri, so that I get the episode I asked for rather than the show from the top.
20. As a user, I want an edit to a note to leave its frontmatter, wikilinks, embeds and block ids intact, so that my vault does not quietly lose links.
21. As a user, I want a new note placed in the folder where that topic already lives, so that my tree stays the way I organised it.
22. As a user, I want the assistant to ask which mount a path belongs to rather than guessing, so that a file operation does not land on the wrong machine.
23. As a user, I want a correction I make to be applied and the stale fact dropped, so that I do not have to correct the same thing twice.
24. As a user, I want memory handled without being narrated, so that a reply is an answer rather than bookkeeping.
25. As a voice user, I want a spoken reply to stay short and still repeat the number, name or time I gave, so that I can confirm it heard me without listening to a paragraph.
26. As a voice user, I want no markdown, paths, entity ids or urls spoken aloud, so that the reply is listenable.
27. As a user, I want a research question to go out to a subagent with everything it needs, so that the worker is not missing context the parent had.
28. As a user, I want a single trivial lookup done directly, so that delegation does not add latency to something faster done in place.
29. As a user, I want a web request to search, open, read and only then act, so that an action is taken against a page that was actually read.
30. As a developer, I want a scenario written as typed code, so that renaming a tool or a mount breaks the scenario instead of rotting it.
31. As a developer, I want to declare the calls a scenario tolerates, so that "it did nothing unnecessary" is something a test decides rather than a reviewer's impression.
32. As a developer, I want to declare ordering only where order is part of the contract, so that concurrent tool invocation is not reported as a failure.
33. As a developer, I want a per-scenario ceiling on tool calls, so that a model that flails and then answers correctly does not pass.
34. As a developer, I want a scenario to declare how many runs must pass, so that one unlucky sample neither reds the suite nor is retried until green.
35. As a developer, I want a failing scenario to stop as soon as it cannot reach its threshold, so that a broken scenario costs less than a working one.
36. As a developer, I want to prove a new scenario can fail before trusting it, so that the suite does not fill up with tests that pass with the prompts removed.
37. As a developer, I want the harness's own checks tested deterministically, so that a broken check does not make the whole suite green and worthless.
38. As a developer, I want the turn to reach the model decorated the way a channel decorates it, so that room-targeting and time-of-day rules are tested as they actually run.
39. As a developer, I want one pinned instant across the agent and the servers, so that expected fire times and run times are exact values rather than ranges.
40. As an AFK agent picking this up, I want the fake for each family named in advance, so that I do not invent a second way to fake a subsystem that already has one.

## Implementation Decisions

**Agent construction goes through the real factory.** A scenario asks `MultiAgentFactory` for an
agent built from the shipped `AgentDefinition`, so domain tools, feature config, the prompt cache,
outposts and the subagent factory are wired exactly as they are in the deployment. Reconstructing
that wiring inside the harness was rejected: it would test a composition no deployment uses. Only
the configured MCP endpoint urls are rewritten, at fixture setup, to the in-process servers'
ports.

**One new production seam: a tool-invocation observer.** `ToolApprovalChatClient` already
overrides function invocation and holds the tool name, the arguments, the result and any
exception at that moment. It gains an optional observer, resolved from DI, absent by default and
no-op in every deployment. The harness registers a recording implementation. Reassembling calls
from streamed function-call and function-result content was rejected as re-parsing what the
function-invoking client already parsed; a harness-built client chain was rejected with the
factory decision above.

**Fakes sit at the outermost externals, per ADR-0030.** The real MCP servers are hosted
in-process. Home Assistant is faked at its client and holds entity state; Music Assistant is
faked at its server; the voice hub is faked at its HTTP handler; the vault is a seeded temp
directory; the subagent factory returns a canned result; web browsing points at a static site
served by the test host with a fake search server whose results point into it; Redis comes from
the existing fixture.

**A claim is a declared, cited unit, per ADR-0031.** Each prompt section declares its claims as
`(id, one-line statement)` beside its prose; the prompt manifest aggregates them; every scenario
cites the claim ids it exercises; a coverage test fails on a declared claim with no scenario.
Claims are declared for all families up front, with the uncovered ones in an explicit exemption
list carrying a reason. The existing prompt-rule vocabulary is untouched — it names the topic a
section legislates for conflict arbitration, which is a subject rather than an assertion.

**A scenario is a typed record in a table**, supplied to a theory. Tool names, mount roots and
claim ids stay compile-time references. It declares: the turn (sender, room, satellite,
timestamp, dismissed alert), the pinned instant, an optional recall block, required calls with
argument and path matchers, permitted calls by tool and path pattern, ordering constraints as
"A before B" pairs, a tool-call ceiling, an expected entity-state diff where relevant, reply
checks, cited claims, `k` of `N`, and a tier.

**Unnecessary is defined by the permitted set.** Required plus permitted is the whole tolerated
surface; any other call fails. A forbidden-set design was rejected because it cannot fail when a
newly added tool starts being called for no reason.

**The turn is decorated as a channel decorates it**, through the existing decoration function
with the pinned instant and time zone. This is what carries the current time to the model — the
system prompt deliberately carries only the date, because a per-turn clock in the system prompt
would invalidate the prompt cache on every turn.

**One pinned instant** is shared by the agent, the timers server and the scheduling dispatcher, so
expected fire and run times are exact strings. A fake time provider that never advances, not the
arming clock used elsewhere.

**Stochasticity is handled by `k` of `N`, never by retrying until green.** Runs stop as soon as
`k` is unreachable and report the honest partial rate. The existing retry-until-positive helper
is not used here: half these assertions are negative, and retrying past a failure hides exactly
the regression being hunted. Sampling parameters stay at provider defaults, since pinning
temperature would add a knob production does not use.

**Two tiers over the same scenarios**, selected by trait: a smoke tier of one canary per family at
a single run, and the full tier at each scenario's declared thresholds.

**Gating**: a new eval category that is opt-in by explicit filter and does not run on a bare test
invocation even when an OpenRouter key is present — deliberately unlike the existing LLM category,
which does. The project instructions' "just run the tests" line needs a note recording that.

**Diagnostics**: a failed run writes a self-contained dump into a gitignored output directory —
the assembled system prompt, the model and provider actually used, the decorated turn, every
recorded call with arguments and results, the final reply, and the failing assertion — and its
path goes into the assertion message. A full pass writes one JSON scorecard: model, timestamp,
per-claim pass rate. Successes are not archived.

**Reply checks are deterministic** — sentence and word limits, absence of markdown, emoji, paths,
entity ids and urls, and the survival of values the scenario declares, accepting any declared
spelling. An LLM judge is a later opt-in second pass over an already-written recording, binding
when it runs, using a different model than the one under test, with its reason string landing in
the failure message.

**Delegation is judged at the parent.** The subagent factory is stubbed; the assertions are
whether the parent delegated at all, which profile it chose, and whether the prompt it wrote is
self-contained.

**Memory is injected as data.** The scenario declares the remembered facts and they ride the turn
the way recall already does. No embedding service participates, so the no-cross-provider-fallback
rule is untouched by the eval.

## Testing Decisions

A good test here asserts what the agent did and said, never how the agent is built. Every
assertion reads the recording and the fakes' observable state — the tool calls with their
arguments, the entity states before and after, the final reply text. No assertion reaches into
the agent's internals, the prompt assembly or a server's storage, because all of those are free
to change without the contract changing.

Two distinct bodies of test:

1. **The harness itself is deterministic code and is built test-first** against a scripted chat
   client replaying canned tool-call sequences. It proves the permitted-set check fails on an
   extra call, the ordering check fails when B precedes A, the ceiling fails one call over, the
   recorder captures a call whose tool threw and a call naming a tool that does not exist, the
   `k`-of-`N` runner stops when the threshold is unreachable, and the dump contains what it
   promises. This is the only legitimate use of a scripted client — it tests the harness, never
   the prompts.
2. **The scenarios themselves are not test-first in the usual sense**, because their subject is a
   real model. Instead, every new scenario must be demonstrated red once by deleting the prose of
   the claim it cites, with the result noted in the commit that adds it. A scenario that stays
   green without its own claim's prose is measuring the model's default behaviour and is deleted
   or sharpened.

Prior art in the repo to follow rather than reinvent: the prompt staleness tests, for the pattern
of walking prose against the code it names; the prompt snapshot fixture, for assembling a
configured agent's real prompt with a fixed clock; the existing LLM-category agent tests, for
driving a real model through the agent against an in-process server; the library server fixture,
for hosting an MCP server in-process on a free port; the existing Home Assistant client fake,
Music Assistant server fake, voice hub stub handler, agent factory fake and Redis fixture, for
the externals.

Modules covered by scenarios: the timer, scheduling and Home Assistant prompt contracts; the
media contracts for playlists and podcast episodes; the vault contract; mount and capability
discovery; memory correction; voice formatting; the web workflow; and subagent delegation.
Modules covered by deterministic tests: the recorder, the permitted-set check, the ordering
check, the ceiling, the run policy, the dump writer, the scorecard writer, and the claim-coverage
test.

## Out of Scope

- Memory retrieval quality. Recall is injected as declared data; whether the right facts would
  have been retrieved is a separate subject with its own tests.
- Real subagent execution. Delegated work running end to end is a different test with a different
  subject.
- Live internet. The web family browses a site the harness serves.
- The LLM judge. Designed here, opt-in, and built after the deterministic suite is trusted.
- Continuous integration. The repo has no CI; the suite is something a person runs deliberately.
- Turn-level budgets beyond the per-scenario call ceiling. The wider budget work — elapsed time,
  subagent count, retries — remains its own item.
- Channel transport behaviour. The boundary is the agent; what a channel does with a reply after
  the agent produces it is covered by the existing channel and end-to-end tests.
- Prompt token budgets, snapshots and staleness, all of which already exist.

## Further Notes

The families that discriminate between mechanisms — timer versus calendar versus scheduled
action — do not discriminate between *tools*. Timers, schedules and Home Assistant are all
reached through the same handful of virtual-filesystem tools, and what separates them is the
**path** the model writes to. "Which tool was selected" is, for most of this suite, "which path
was written to", and the argument matchers carry nearly all of the signal.

The design decisions behind this spec are recorded in ADR-0030 (real model, real servers, fakes
at the externals) and ADR-0031 (claims declared beside their prose). The vocabulary — claim,
scenario, recording, permitted set — is in the glossary under "Behavioural evaluation". This work
is Priority 1 of the agent performance review in this directory's sibling file; Priorities 2 and 3
of that review are already landed.

Build order: the harness machinery first, then claims for the timer contract with the coverage
test and exemption list, then the in-process fixtures for that family, then the timer, calendar
and scheduled-action scenarios, then the remaining families.

## Tickets

Sixteen tickets in `issues/`, numbered in dependency order: 01 the observer seam, 02 the first
scenario end to end, 03–06 the assertion vocabulary (run policy and failure dump, permitted set
and call ceiling, claims and coverage, ordering), 08 the scorecard, and 07 plus 09–16 one per
scenario family.
