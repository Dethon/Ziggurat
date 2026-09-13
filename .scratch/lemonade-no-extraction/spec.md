# A turn addressed to the Lemonade chat host is never extracted from

Status: done
ADR: `docs/adr/0042-a-turn-addressed-to-the-local-box-is-never-extracted-from.md`

## Problem Statement

A person picks a Lemonade model in WebChat for the reason anyone runs a model on their own
machine: what they type goes to their box and no further. The deployment does not honour that.

Memory extraction is enqueued from the recall hook before the agent runs, and the extraction that
follows reads the current **user** message and sends it to a hosted model. So the half of the turn
the person chose the Lemonade chat host to protect — their own words — leaves the machine anyway,
summarised into storage by a cloud provider. A turn that never touched the cloud on its way out is
sent to the cloud on its way to memory.

The same person has no way to notice this, and a second failure hides it further. A patchable
model is only honoured if it is in the list the agent offers; that list is recomputed from live
discovery, and discovery fails closed, so any outage of the box empties it. A Lemonade model that
is absent — stale, or merely unreachable for a minute — is silently rejected and the turn runs on
the agent's own hosted model instead. The person asked for their box, got the cloud, and was told
only in a log line.

## Solution

A turn addressed to the Lemonade chat host writes nothing to memory, and is never quietly served
by a hosted provider in its place.

Extraction is skipped for any turn whose patchable model is a Lemonade model. The skip reads what
the turn *asked for* rather than what served it, so it holds even when routing falls back.

Falling back stops being silent. A turn that asks for a Lemonade model the host does not offer
fails, and the person is told their box could not serve it, rather than being answered by a hosted
model they did not choose.

Recall is deliberately untouched. A Lemonade turn still receives its recall block. The boundary is
one-way on purpose: the box may read the person's remembered facts; nothing from a Lemonade turn
is sent out to be summarised.

## User Stories

1. As a person chatting through a Lemonade model, I want my message never sent to a hosted
   extraction model, so that choosing my own box actually keeps my words on it.
2. As a person chatting through a Lemonade model, I want the agent to still remember what it
   already knew about me, so that picking the local box does not cost me a stranger for a
   conversation partner.
3. As a person chatting through a Lemonade model, I want nothing new written to memory from that
   conversation, so that the record of what I said locally does not accumulate in shared storage.
4. As a person whose Lemonade chat host has gone down mid-session, I want the turn to fail
   visibly, so that I can choose whether to re-ask a hosted model rather than having that chosen
   for me.
5. As a person whose Lemonade chat host has gone down mid-session, I want my message not sent to a
   hosted extraction model on the way, so that an outage of my box does not become a disclosure.
6. As a person who picked a Lemonade model that the host has since removed, I want to be told the
   model is gone, so that I can pick one that is actually there.
7. As a person chatting through a hosted model, I want extraction to work exactly as it does
   today, so that the local-box behaviour costs me nothing on my normal turns.
8. As a person whose patchable model was rejected for any reason other than being a Lemonade
   model, I want the turn to proceed as it does today, so that a drifted model list does not start
   failing turns that used to work.
9. As a person who sets a reasoning effort the agent does not recognise, I want the turn to
   proceed on the agent's own effort, so that the stricter Lemonade rule does not leak into
   unrelated patch fields.
10. As an unremembered user talking to a Lemonade model, I want to stay unremembered by that
    conversation, so that the local box is not the thing that first enrols me into memory.
11. As an operator, I want a Lemonade turn's absence from extraction to be visible as a gap in the
    existing per-model metrics, so that I can confirm the boundary is holding without new
    instrumentation.
12. As an operator, I want no new configuration to manage, so that the boundary cannot be
    misconfigured into being off.
13. As an operator with no Lemonade chat host configured, I want nothing about my deployment to
    change, so that the feature is inert where the host does not exist.
14. As an agent whose `memory` feature is disabled, I want the Lemonade rule to be irrelevant, so
    that the two gates do not interact.
15. As a developer reading the recall hook later, I want the Lemonade skip to sit beside the
    existing feature gate, so that the reasons extraction does not happen are all in one place.
16. As a developer, I want the memory anchor's contract untouched, so that the pinned guarantee
    that recall runs before the turn is persisted still holds.
17. As a developer, I want the two halves of this change to ship together, so that neither failure
    mode is left open by a partial rollout.
18. As a future reader, I want the one-way read/write asymmetry written down, so that I do not
    "fix" it into symmetry and strip the local model of context.
19. As a future reader, I want the reason extraction was not simply routed to the local model
    recorded, so that the obvious symmetry is not re-proposed.
20. As a person reviewing the glossary, I want the Lemonade chat host entry to stop describing
    extraction as its exception, so that the vocabulary matches the behaviour.

## Implementation Decisions

**The predicate is the Lemonade model, read from the turn's patchable model.** There is one local
host and the honest rule names it. The existing helper that identifies a Lemonade model by its
namespaced form is the single point of truth; no new marker, no capability inference, and no
policy property invented for a second local backend that does not exist yet.

**The gate reads the request, not the outcome, and therefore fails closed.** Extraction is
enqueued from the recall hook, which runs before the agent and before any host is chosen — the
patchable model on the message is all there is at that moment. A turn that asked for a Lemonade
model is not extracted from even if it was ultimately served by a hosted model.

**The extraction skip lives in the recall hook, beside the per-agent `memory` feature gate.** Same
seam, same shape: one more reason the enqueue does not happen, stated where the other one is. The
gate returns before the memory anchor is taken, so the anchor's correctness is untouched.

**Recall is not gated.** The hook still builds its recall block and attaches it. Only the enqueue
is skipped. This is the one-way boundary and it is deliberate.

**A rejected Lemonade model fails the turn.** Model-override resolution currently returns nothing
for an unknown patchable model and lets the turn proceed on the agent's own. For a Lemonade model
it instead raises the existing Lemonade chat host failure, whose construction already defeats the
WebChat transient-error filter, so the person sees it.

**The failure is scoped to the Lemonade model case alone.** Model-override resolution shares its
rejection path with reasoning-effort resolution. A rejected hosted model id and a rejected effort
keep today's warn-and-continue. Only a rejected Lemonade model throws. Widening this would turn
every client with a drifted model list into failed turns.

**No new configuration.** The boundary is not a tunable. There is no setting to disable it,
because a setting to disable it is a setting to leak.

**Existing memories are left alone.** A stored memory records the conversation it came from and
nothing about the model or host that served it, so Lemonade-derived entries cannot be identified
without replaying transcripts. They predate the boundary and stay.

**Two documents change with the code.** The glossary's Lemonade chat host entry currently ends by
naming memory extraction as the one exception to "nothing answers in its place" — that sentence
becomes false and is removed. The memory architecture rule attributes the extraction enqueue to
the chat monitor; it is the recall hook that enqueues, and the correction lands here because the
gate goes into the method the rule misnames.

## Testing Decisions

A good test here asserts what an outside caller can observe — that nothing was enqueued, that a
recall block was still attached, that a turn raised rather than reaching a host — and never that a
particular branch was taken. Both seams already exist and both already contain near-identical
prior art to mirror.

**Seam 1 — the recall hook's unit tests, for the extraction gate.** The file already holds a
family of tests asserting that extraction *is* enqueued under awkward conditions: when embedding
fails, when the user is unremembered, when thread context is unavailable, and with an anchor equal
to the persisted count. The new tests are that family's negative. Cases:

- A message patched to a Lemonade model enqueues nothing.
- The same message still gets its recall block — asserted in the same test, because the one-way
  boundary is the point and splitting it lets a future change break half of it silently.
- A message patched to a hosted model still enqueues, unchanged.
- A message with no patchable model at all still enqueues, unchanged.
- An unremembered user on a Lemonade turn still enqueues nothing.

**Seam 2 — the host-routing chat client's unit tests, for the loud fallback.** This file already
drives a real agent through a streaming run with a message patched to a Lemonade model, against
stubbed hosts, asserting on the bytes each host received. A model absent from the offered list is
a variation on that setup. Cases:

- A Lemonade model the agent does not offer raises the Lemonade chat host failure, and the hosted
  stub captured nothing.
- A hosted model id the agent does not offer still falls back quietly to the agent's own model, as
  today.
- An unrecognised reasoning effort still falls back quietly, as today.
- A Lemonade model the agent *does* offer still reaches the host — the existing tests cover this
  and must stay green.

**Two seams rather than one.** The halves sit in different modules: one in the memory subsystem
before the agent runs, one in the agent's own model resolution. A single test spanning both would
be an integration test asserting two unrelated things, with slower and vaguer feedback than either
unit test gives.

**No new seam for the anchor.** The pinned contract that recall runs before the turn is persisted
is untouched by a gate that returns earlier than the anchor is taken. Its existing test must stay
green and gets no additions.

## Out of Scope

- Purging or identifying Lemonade-derived memories already in the store.
- Any change to recall: the recall block still reaches Lemonade turns, by decision.
- Routing extraction to a local model. Considered and rejected on the box's resources; see the ADR.
- A general "this route does not extract" policy property. Deferred until a second local backend
  makes the concept real.
- Dreaming and consolidation, which read the store rather than the turn and are unaffected.
- The other Lemonade — the deployment's own service for speech, dictation and embeddings. Memory
  recall's embeddings still go there and must keep doing so.
- Making discovery fail open, or narrowing the outage window that now costs turns. The cost is
  accepted in the ADR.
- Any WebChat change. The lemon marker and the model list already render from existing state.

## Further Notes

The sharpest consequence is worth restating: discovery fails closed, so during any outage of the
box **every** Lemonade model is rejected, not merely stale ones. Before this change that cost a
silent reroute; after it, those turns fail. That is the accepted trade — never routing a local
turn to a hosted provider unannounced — and it is the thing most likely to be reported as a
regression by someone who has not read the ADR.

The two halves must land together. The extraction gate alone would silently drop extraction for
turns a hosted model really did serve; the loud fallback alone leaves the outage window open. Each
closes the other's failure mode, which is why they are one spec rather than two.

Extraction reads only the current user message and is forbidden to treat assistant content as fact
about the user. So what this change withholds is precisely the person's own words — not the local
model's output, which was never extracted from in the first place.
