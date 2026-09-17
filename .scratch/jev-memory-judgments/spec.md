# Jev memory judgments

Status: ready-for-agent

Grilled 2026-09-18. Second of the five Jev uses (`.scratch/jev-skill-preload/spec.md` names the
order). Reuses that spec's typed-judgment contract and its one TypeSafe client unchanged — no
score type is needed here. Rules: `.claude/rules/memory-architecture.md`. Boundary:
`docs/adr/0042-…`. Probe and its results: `probe/`.

## Problem Statement

Memory writes more than it should and checks nothing it writes. Every user turn pays a luna call
whose prompt spends forty lines insisting that "empty is the correct answer most of the time";
whatever comes back is stored — no code reads a candidate's importance or confidence, and the only
filter is a same-category embedding match at 0.85. The extraction prompt lists the junk it was
seen producing, and the agent ships a tool for sweeping it out afterwards. At night, memories are
clustered by a flat 0.60 cosine with no size limit and handed to a model told to "prefer merging
when in doubt"; what it decides is applied by deleting the sources, and nothing in the code can
say no.

Underneath all of it the extractor's window is wrong. A tool call and its result render as two
blank lines — the result labelled `user:` — and each takes one of the six slots, so a turn that
used two tools is the whole window and the conversation before it is gone.

## Solution

Three small typed judgments from Jev, all off the reply path, each failing toward what happens
today.

**A gate before extraction.** Three yes/no questions about the current message: does it state a
lasting fact about the person, a standing preference, a standing instruction or correction. When
all three come back at 0.1 or below, the luna call is skipped. Anything else — unsure, late, down
— extracts as today, because a skipped memory is gone and a wasted call is a fraction of a cent.

**A check on every candidate.** Four yes/no questions against the same window: did the person
actually say this, is it about them, would it still be true and useful in six months, is it
something other than a record that they asked for something. A candidate is stored only when each
is at least 0.5. A standing instruction is checked for the first alone — an instruction *is* a
request, and the probe showed the fourth question rejecting every one. What is dropped is
published with its scores. The embedding dedup still runs after.

**A relation for every pair in a cluster.** Cosine still proposes clusters. One request per
cluster asks, for each pair, a four-way choice: same fact, updates or contradicts, related but
distinct, unrelated. Only memories linked by the first two reach the merge model together, and a
merge it then proposes is applied only if its sources are so linked — the first reason the code
has ever had to refuse a destructive merge. With Jev unreachable the night runs exactly as it
does now.

First, the window is fixed for every reader: tool-role messages and assistant messages that carry
only a tool call are left out, and the slots count conversation.

Measured 2026-09-18 on a synthetic Spanish and English set (`probe/`), `jev-1.13.0`: the gate
skipped 10 of 12 empty windows and none of 14 that held a memory; the check dropped all 13 junk
candidates — the prompt's own four bad examples and the eval's four noise facts among them — and,
with the instruction rule, kept all 11 keepers; the pair relation made the right link decision on
15 of 16, its one miss leaving both memories in place.

## User Stories

1. As a person, I want something I never said never stored as a fact about me, so that what the agent remembers is what I told it.
2. As a person, I want "¿qué tiempo hace?" to leave no memory that I asked about the weather, so that my store is not a log of my requests.
3. As a person, I want "a partir de ahora háblame de tú" always kept, so that no check ever costs me a standing instruction.
4. As a person, I want a short answer to the agent's question — "a las siete siempre" — understood with the question it answers, so that the gate and the check read it as the fact it is.
5. As a person, I want a thing I mention in passing inside a command — "apaga la luz, por cierto me he mudado a Valencia" — still extracted, so that the gate never skips a turn that holds something.
6. As a person, I want "vivo en Valencia" to replace "vive en Madrid" rather than sit beside it, so that the pair relation keeps contradictions together for the merge model to resolve.
7. As a person, I want my sister's city and my brother's city never merged into one memory, so that similar is not the same.
8. As a person, I want a merge that came back with no text to delete nothing, so that a model's blank answer cannot erase what it was given.
9. As a person, I want memory to work when TypeSafe is down exactly as it works today, so that a third party can never stop it or make it worse than it was.
10. As a person, I want what I say to the local box to reach neither luna nor Jev, so that the ADR 0042 boundary covers the new calls without a second gate to forget.
11. As a person, I want no text a tool fetched — a web page, a vault note, a device dump — put in front of the model that writes facts about me, nor sent to TypeSafe.
12. As a person, I want the conversation before a tool-heavy turn still in the extractor's view, so that two tool calls do not cost the window its context.
13. As the maintainer, I want every bar in `appsettings.json`, so that tuning is a config edit with a probe run behind it.
14. As the maintainer, I want the extraction event to say whether a turn was gated, came back empty, produced candidates or failed, so that "found nothing" and "broke" stop sharing a zero.
15. As the maintainer, I want every Jev call published — which judgment, the verdicts, a dropped candidate's text and scores, a refused merge's ids, latency, tokens — so that every bar can be re-read from production.
16. As the operator, I want the Memory page to chart outcomes, candidates, drops and refused merges, so that a gate that started skipping too much is something I see.
17. As the maintainer, I want the probe kept as a test over synthetic data only, so that a Jev bump or a question edit is checked in a minute and no real conversation is ever in the repo.

## Decisions

### The window (every reader)

- `ExtractionWindow.Build` leaves out tool-role messages and assistant messages with no text.
  `windowSize` counts what remains. Nothing is rendered in their place.
- Rendering tool results as capped `tool:` lines was considered and set aside: it has never been
  the behaviour, the assistant's reply normally restates what was fetched, and it would put
  fetched text before the memory writer and send vault content to TypeSafe. It is the named
  follow-up if a real miss shows.
- Jev reads the same window as fields, never the rendered string:
  `{ "context": [{role, text}…], "current": "<the user message>" }`, plus `candidate` for B.

### A — the gate

- In `MemoryExtractionWorker`, after the window is built, beside the existing empty-window
  return. It inherits the Lemonade boundary because a Lemonade turn is never enqueued; the spec
  relies on `MemoryRecallHook`'s gate staying where ADR 0042 put it.
- Nouls `fact`, `preference`, `instruction`, worded as in `probe/jev_memory_probe.py`. Skip iff
  every one ≤ `gate.skipAtOrBelow` (0.1). Absence of an answer is not a skip.

### B — the check

- In the worker between extraction and the store fan-out, one request per candidate, in parallel
  as the fan-out already is. Nouls `supported`, `about_user`, `durable`, `not_a_question`. Store
  iff each ≥ its bar (0.5 each, separately configurable).
- `MemoryCategory.Instruction`: `supported` alone.
- Absence of an answer stores as today. The 0.85 same-category dedup is unchanged and runs after.

### C — the pair relation

- In `OpenRouterMemoryConsolidator`, after cosine clustering. One request per cluster: state is
  the cluster's memories by index, one choice per pair (`same`, `updates`, `distinct`,
  `unrelated`). A cluster over `dreaming.maxClusterMemories` (12 → 66 pairs) is judged on its 12
  most similar to the centroid; the rest wait for a later pass.
- Links are `same` and `updates`. The connected components of the link graph, singletons dropped,
  are what the merge model is called with.
- `MemoryDreamingService` applies a `Merge` or `SupersedeOlder` only if every source is in one
  linked component of that pass; otherwise it is refused, logged by id, and published.
- Absence of an answer for a cluster: that cluster goes to the merge model as cosine made it and
  its decisions are applied unvetoed — today's behaviour, the user's choice.
- A `Merge` whose merged content is blank is refused and its sources kept, Jev or no Jev.

### Settings

`memory: { judgments: { enabled, gate: { skipAtOrBelow }, verify: { supported, aboutUser,
durable, notAQuestion }, pairs: { maxClusterMemories } } }` in `Agent/appsettings.json` alone.
The key, URL and pinned model are `typeSafe`'s, from the first spec. No deadline pressure here:
the client's default timeout, no in-turn budget.

### Telemetry

- `MemoryExtractionEvent` gains `Outcome`: `gated`, `empty`, `extracted`, `failed`. A retry
  exhaustion is `failed`, no longer a silent zero.
- One `MemoryJudgmentEvent` per Jev call: kind (`gate`/`verify`/`pairs`), verdicts, for a drop the
  candidate's content and scores, for a refused merge its source ids, latency, input tokens,
  answered or absent.
- `MemoryMetric` gains what the Memory page needs to chart: candidates, outcomes, drops, refused
  merges.

### Acceptance

- `probe/`'s three sets become a `[Trait("Category","Jev")]` test over the real question
  builders, skipped without a key: zero false skips; every labelled keeper kept and every labelled
  junk dropped; every link decision right but the one recorded conservative miss, which may only
  ever fail toward "not linked".
- The existing `Category=Llm` memory tests stay green with judgments on.
- One week after deploy the bars are re-read from the new events and the result written under
  this spec's Comments.

## Out of Scope

- Rendering tool results into the window.
- Production conversations as probe data — synthetic only, by decision.
- Provenance, tombstones or soft delete for merges; an importance or confidence threshold; the
  cross-category hole in the 0.85 dedup.
- Shadow mode. A and B act from the first deploy.
- Replacing cosine clustering, or the merge model writing merged text.
- Recall. ADR 0019 stands: nothing hosted on that path.
