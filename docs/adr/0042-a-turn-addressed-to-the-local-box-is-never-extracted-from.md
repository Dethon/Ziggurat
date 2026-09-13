# 0042 — A turn addressed to the local box is never extracted from

Status: accepted
Date: 2026-09-13

## Context

The Lemonade chat host is somebody's own machine on the LAN, reached by patching a turn's model to
a `lemonade/`-prefixed id (`Domain/Agents/LemonadeModelId.cs`). A person picks it in WebChat for
the reason anyone runs a model locally: what they type goes to their own box and no further.

Memory extraction does not honour that. It is enqueued from the recall hook before the agent runs
— `ConversationGroup.BuildUserMessageAsync` calls `MemoryRecallHook.EnrichAsync`, which queues a
`MemoryExtractionRequest` — and the worker then sends the user's own words to a hosted model,
`Memory:Extraction:Model`, currently an OpenRouter id. Extraction reads the `[CURRENT]` **user**
message and is forbidden to treat assistant content as fact about the user
(`Domain/Prompts/MemoryPrompts.cs`), so what leaves the machine is exactly the half the person
chose the local box to keep. A turn that never touched the cloud on its way out is summarised by
the cloud on its way to storage.

Routing extraction to the local box instead was considered and rejected on the box's own terms:
a second model loaded beside the chat model, a second prompt prefix, and the cache thrash that
follows cost more than the memories are worth on hardware that is already the constraint.

The gate has to read the turn's *requested* model, because at enqueue time that is all there is.
The recall hook runs before the agent, so the host that will actually serve the turn has not been
chosen yet — only the patch the message carries, stamped a few lines earlier in the same method.

That matters because the requested model and the serving host can disagree. `ResolveModelOverride`
validates the patch against `IPatchableModelSource.Ids` and, when the id is absent, returns null
after a warning — the turn then runs on the agent's own model, on OpenRouter. The whitelist is
recomputed per read from live discovery, and `LemonadeModelDiscovery` fails closed: any outage
empties the list. So a person can ask for the local box, get the cloud, and be told only in a log
line nobody is reading.

## Decision

**A turn addressed to the local box is never extracted from, and never quietly served by the
cloud instead.**

Two halves, both load-bearing:

The recall hook skips the extraction enqueue when the message's config patch names a `lemonade/`
model, beside the existing per-agent `memory` feature gate — the same seam, the same shape. The
gate reads the request, not the outcome, and therefore fails closed: a turn that *asked* for the
local box is not extracted from even if routing fell back to OpenRouter.

A `lemonade/` patch naming a model the whitelist does not hold no longer falls back. It throws
`LemonadeChatHostException`, whose `From` already rewrites cancellations so WebChat's transient
filter cannot swallow it, and the person is told their box could not serve the turn. This is
scoped to the Lemonade model case alone: a rejected `reasoningEffort`, or a rejected OpenRouter
model id, keeps the existing warn-and-fall-back through `LogRejectedPatch`.

Neither half is sufficient alone. Without the fail-closed gate, an outage silently sends local
words to the cloud extractor. Without the loud fallback, fail-closed silently drops extraction
for turns OpenRouter really did serve. Together the two failure modes cancel: the only turns that
lose extraction are turns that were refused.

**Recall is deliberately not suppressed.** A Lemonade turn still receives stored memories as
context. The boundary is one-way on purpose — the local box may read the user's history, and
nothing from a local turn is sent to the cloud to be summarised. The asymmetry is the point:
what is being withheld is what leaves the machine, not what enters it.

## Considered options

**Route extraction to the local model.** The obvious symmetry. Rejected for the box's resources:
a second model beside the chat model thrashes the prompt cache, and the machine's performance is
the constraint the local path exists to respect.

**Gate on the model that actually served the turn.** Accurate rather than conservative. It would
mean moving the enqueue after the turn or resolving the override inside
`BuildUserMessageAsync` — and the anchor's correctness depends on recall running before the turn
is persisted, which `MemoryAnchor`'s factory names and `ChatMonitorMemoryAnchorTests` pins.
Rejected as a large change to a pinned contract, to buy accuracy in a case the loud fallback
removes anyway.

**A named policy property rather than the backend.** `extractMemories: false` on a route, which
Lemonade would merely set, surviving a second local backend. Rejected as vocabulary for a concept
that does not exist yet: there is one local host, and the honest rule is about that host. If a
second appears, the property can be introduced then, with two instances to shape it.

**Extract but quarantine.** Tag the entries and filter at recall. Rejected outright — the harm is
the send, not the store. Quarantine pays the cloud call in full and protects nothing.

**Suppress recall as well.** Symmetric and simpler to explain. Rejected because it strips the
local model of all user context for no gain against the stated harm; the box is trusted with the
history, the cloud is not trusted with the turn.

**Keep the silent fallback, notice it in the reply.** No lost turns. Rejected because there is no
channel-visible notice mechanism for a mid-turn routing change, and inventing one to explain a
degradation is more machinery than refusing it.

## Consequences

- A Lemonade turn writes nothing to memory. The person gains no memories from the hours they
  spend on the local box, and that is the trade they are making by choosing it.
- A discovery blip now fails turns that would previously have degraded to OpenRouter. `Lemonade
  ModelDiscovery` empties its list on any outage, so during one **every** Lemonade patch is
  refused, not just stale ids. This is the accepted cost of never routing a local turn to the
  cloud unannounced.
- Memories already extracted from Lemonade turns stay. `MemorySource` carries a conversation id
  and a null message id, with no model or host field, so they cannot be identified without
  replaying transcripts — and the store predates the boundary this ADR draws.
- Metrics keep separating the hosts for free: the namespaced id survives everywhere but the wire,
  so a Lemonade turn's latency and token events already stamp under `lemonade/…`. Extraction's
  absence is visible as the gap.
- `.claude/rules/memory-architecture.md` attributes the extraction enqueue to `ChatMonitor`; it
  is the recall hook that queues. The line is corrected alongside this change, since the gate
  lands in exactly the method the rule misnames.
- **The client had to stop sanitizing for the loud fallback to mean anything.** WebChat's
  `AgentSettingsSelectors.Sanitize` replaced a model the catalogue stopped listing with the
  agent's default on every catalogue push. Discovery failing closed empties every Lemonade model
  at once, so in exactly the outage this ADR is about, the person's pick was swapped out before
  they could send a turn — and that turn then went to a hosted provider carrying no patch at all,
  unrefused and extracted from. `Sanitize` now leaves the model alone, for hosted picks too: no
  pick is ever edited behind the person's back, and the server is what says what happened to it.
  A stale reasoning effort still falls back, and a client with nothing stored still takes the
  agent's default — neither is a pick.
- **The boundary is per-enqueue, and that is the whole of it.** A Lemonade turn persists like any
  other, so a later hosted turn's `ExtractionWindow` can carry its messages as `[context -N]`.
  That is intended and not a leak: the person went back to a hosted model, and the conversation
  they are having there is the conversation they are having. What this ADR withholds is the turn
  addressed to the box, not every sentence the box ever saw. "The harm is the send" above governs
  the enqueue that turn would have caused — it is not an argument for scrubbing history.
