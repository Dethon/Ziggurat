# 0020 — An attachment is stored once and hydrated on the way out

Status: accepted
Date: 2026-08-09

## Context

WebChat is gaining attachments: images and PDFs sent with a turn. The obvious implementation is
to put the bytes on the user message, because that is what the model receives and what the
history already stores. It is the wrong one, and the reason is the history read rather than the
token bill.

A conversation's history is a Redis list with one JSON `ChatMessage` per element.
`RedisThreadStateStore.GetMessagesAsync` reads the whole key and deserializes every element on
every turn, and `RedisStateService.GetHistoryAsync` reads the same list every time a browser
opens the topic, keeping only `TextContent` and discarding everything else. A 2 MB photo is
about 2.7 MB of base64 in one element. Three photos in a topic means roughly 8 MB crossing the
socket and going through `JsonSerializer` on every turn for the 30 days the history lives, and
the browser pays the same cost to throw the bytes away.

The token side does not argue the same way. An image is roughly 1,000 to 1,500 tokens; at
`openai/gpt-5.6-luna`'s $0.10 per million prompt tokens that is about $0.00015 each time it is
sent, and the conversation's stable prefix is prompt-cached under the `session_id` this repo
already sends. Even a PDF that parses to 50,000 tokens is about 6% of the configured 800,000
token context. Attachments are cheap to send and expensive to store.

The bytes also have to exist on disk regardless. The upload store holds them so the agent can
read them, and where the agent has a sandbox they are copied into it as files the model can
work on. Anything kept in the history is therefore a second copy of something already stored.

## Decision

**The history stores an attachment reference, never bytes, and hydration puts the bytes back on
the way out.**

A reference names where the file rests, its media type, its filename and its size. It is what
the persisted `ChatMessage` carries, for good — a history read costs the same whether or not
files were sent, and the browser's history call no longer transfers bytes it discards.

Hydration happens where turn decoration already happens: on the way to the model and never on
the way in. It reaches back a configured number of messages (20 by default) and applies the same
rule to every kind of attachment. Beyond that distance, and for any reference whose bytes are
gone, hydration produces a placeholder naming the file. The reference itself stays in the
history either way, so the record of what was sent never disappears even when the file has.

`MessageTruncator` gains an attachment case in its token estimate. Without one it counts a
50,000-token PDF as a fixed handful of tokens and the context overflows silently.

## Considered options

**Bytes in the history, no bound.** Simplest, nothing to hydrate. Rejected on the read cost
above, which lands on every turn of an active conversation and on every topic open.

**Bytes in the history, stripped in place after N messages.** Bounds the growth and keeps the
recent window self-contained, so a missing file cannot break it. Rejected because it holds a
duplicate of what is already on disk, pays the per-turn read cost during exactly the window
where turns are most frequent, is irreversible once stripped, and needs in-place rewriting of a
list the store only ever appends to and reads.

**Send the bytes only on the turn they arrived on.** Cheapest of all and no hydration distance
to choose. Rejected on behaviour: "now translate the text in that photo" is a normal thing to
ask on the next turn, and it would fail.

**Separate depths for images and documents.** Rejected as configuration nobody will tune. The
context budget is large enough that one distance serves both.

## Consequences

- An attachment stops being visible to the model at a distance measured in messages, while its
  reference stays visible in the transcript forever. Those are different lifetimes and the
  interface has to make the difference legible: a placeholder that names the file, not a gap.
- Hydration reads from the upload store, so the store's retention has to outlast the hydration
  distance in wall-clock terms. Retention is a sweep in days and hydration is a count in
  messages; a busy conversation moves through 20 messages far faster than the sweep, but a
  quiet one does not, which is why the placeholder path is not an edge case to be tidied away
  later.
- Widening hydration later is a configuration change and costs nothing, because the bytes were
  never destroyed. That is the property the stripping alternative would have given up.
