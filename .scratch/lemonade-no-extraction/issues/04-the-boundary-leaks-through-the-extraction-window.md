# 04 — A Lemonade turn's words still reach the hosted extractor, one turn later

**What's wrong:** The gate from ticket 01 stops the enqueue *for that turn*. It does not stop the
next hosted turn in the same conversation from carrying the Lemonade turn's words to the hosted
extractor as context.

`ExtractionWindow.Build` (`Domain/Memory/ExtractionWindow.cs:17-36`) takes
`persistedHistory.Take(anchor).TakeLast(windowSize - 1)`, with `WindowMixedTurns = 6`
(`Infrastructure/Memory/MemoryExtractionWorker.cs:18`). Lemonade turns are persisted like any
other, so up to five prior messages — the person's own words, typed to their own box — are
rendered as `[CURRENT]`/`[context -N]` by `ExtractionWindow.Render` and sent to
`Memory:Extraction:Model` on OpenRouter.

Failure scenario: a person types three messages on `lemonade/…`, then switches back to a hosted
model and sends one more. That turn enqueues correctly. The worker's window contains up to five
prior messages, the three local ones verbatim among them, and they go over the wire.

`MemoryPrompts` forbids the model *extracting facts from* `[context -N]` turns, which is why this
survived review of ticket 01. But ADR 0042 rejects the quarantine option precisely because
**"the harm is the send, not the store"**. This is a send.

**Why it is its own ticket:** fixing it means the persisted history has to carry which turns were
addressed to the box, so the window can drop them — a marker on the message, or a per-turn
origin recorded alongside it. ADR 0042 made no decision about that, and inventing one inside a
review follow-up is exactly the kind of scope creep the spec's Out of Scope section guards
against. It needs its own decision, and probably an amendment to 0042.

**Blocked by:** none, but it should not be built before the design question is settled.

**Status:** needs-triage

- [ ] Persisted turns carry enough for the extraction window to know which were addressed to the
      Lemonade chat host.
- [ ] A hosted turn's extraction window contains no message from a Lemonade turn.
- [ ] The window still fills to `WindowMixedTurns` from the messages that remain, rather than
      shrinking silently.
- [ ] ADR 0042 is amended, or a new ADR supersedes the relevant part: the boundary as shipped is
      per-enqueue, and this makes it per-message.
