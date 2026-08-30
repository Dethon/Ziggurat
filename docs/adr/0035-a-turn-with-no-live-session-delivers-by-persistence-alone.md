# 0035 — A turn with no live session delivers by persistence alone

Status: accepted
Date: 2026-08-30

## Context

The SignalR channel server keeps its sessions, its conversation-to-topic map and its response
channels in process, and nothing but an explicit topic delete ever removes a session — a refresh,
a dropped connection or a closed tab leaves them standing, which is what makes buffered resume
work. So the map misses in exactly three ways: the topic was never opened since the server last
started, the topic was just deleted, or the server restarted while the agent — a separate
container — kept running its turn.

Every one of those is also a miss on the response channels, because a stream is only ever created
through a live session. A chunk arriving on the miss path has no subscriber to lose. The reply is
not lost either: the agent persists its own history to Redis as part of the run, with no
dependency on channel delivery, and the browser reads that history back when it next opens the
topic. Agent-initiated turns — a schedule firing, a download alert — hit this path routinely,
whenever they deliver into a conversation nobody has open.

The code on that path did not say any of this. The miss was coerced (`?? conversationId`) into a
value guaranteed to match nothing — a topic id is a UUID and a conversation id is two hashes
joined by a colon, disjoint by construction — and every chunk logged a warning, hundreds per
offline turn. An architecture review read the warning-and-drop as a live silent-drop defect and
proposed making it fail loudly.

One genuine defect did sit on the same path: an approval request with no live session registered
itself under the coerced value, dropped its prompt unseen, and then awaited an answer with no
timeout. The cancellation sweep filters by topic id and the coerced value matches no real one, so
not even deleting the topic released it. A scheduled agent reaching an approval-gated tool with
nobody connected deadlocked its own run forever.

## Decision

**A turn that finds no live session delivers by persistence alone.**

Streaming is a live-audience concern and the channel drops what no one is subscribed to,
deliberately and quietly: the miss is handled by an explicit null check and logged at debug,
because it is a designed path, not an anomaly. Persistence is the agent's job and already done;
the browser catches up from history.

An approval request on this path is answered immediately with a denial naming the reason, rather
than registered and awaited. A question nobody can see must not hold the turn; the denial is
visible in the persisted reply. Auto-approval notifications stay fire-and-forget and simply do
not render.

## Considered options

**Fail loudly on the miss.** What the review proposed. Rejected because the miss is the normal
shape of every offline delivery — failing it would abort designed behaviour to surface a
condition that costs nothing.

**Buffer or persist on the channel side.** Hold undeliverable chunks until a session appears.
Rejected because the agent's history store already persists the reply; a channel-side copy is a
second writer for one fact, with all the reconciliation that implies (ADR 0018 removed exactly
that shape once).

**Bound the approval wait with a timeout.** Keeps a window for a user who connects seconds
later. Rejected because it still holds the run for the whole window on a prompt that was never
rendered — the wait is not long, it is invisible, and no timeout fixes invisible.

## Consequences

- The `?? conversationId` coercion goes; the three call sites take the explicit
  null-check-and-return shape the conversation-create tool already had.
- A routine offline turn no longer emits a warning per chunk.
- A scheduled agent's approval-gated tool call resolves as denied instead of hanging forever,
  and the run continues.
- A user who opens the topic seconds after a schedule fires still gets no live stream for that
  turn — only the persisted reply on load. That was already true; it is now stated.
