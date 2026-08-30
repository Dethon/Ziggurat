# 02 — The SignalR server carries the identity

**What to build:** The SignalR channel server passes the conversation identity as the type instead of interpolating and re-deriving strings. The hub's composition sites construct it; the session service's maps and lookups go through its spellings so a topic-shaped lookup can no longer be handed a conversation-shaped value; the upload-minting signature takes the identity instead of two adjacent same-typed strings. The three coercing miss-fallbacks in the stream and approval services become the explicit null-check-and-return shape the conversation-create tool already has, logged at debug — per ADR-0035 the miss is the designed offline path, not an anomaly. A connected client sees no change at all.

**Blocked by:** 01 — The identity type composes and parses itself.

**Status:** ready-for-agent

- [ ] Red-first where behaviour changes: a stream-service test asserts the miss path returns early and logs at debug, not warning.
- [ ] No hand-interpolated conversation spelling remains in the hub or the session service; construction goes through the type.
- [ ] The inverse-map sites take the explicit null shape; the coercing fallback is gone from all of them.
- [ ] The upload-minting signature takes the identity; swapping its former two string arguments is now a compile error.
- [ ] All existing hub, session, stream, approval and attachment tests pass untouched — the assertion that a connected client sees no change.
- [ ] An offline turn (no live session) produces no warnings — verified by test on the write path.
