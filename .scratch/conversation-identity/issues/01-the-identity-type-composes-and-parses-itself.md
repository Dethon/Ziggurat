# 01 — The identity type composes and parses itself

**What to build:** The existing domain conversation-identity record becomes the authored home of the identity, on the agent-key pattern: a readonly record struct whose conversation spelling is derived, not stored, with a total render and a partial inverse that returns null for anything that is not a conversation identity — a satellite id, a correlation id, an eval id, a bare GUID. The type owns the non-forum rule (an equal thread and chat means no thread); the narrowing to the Telegram API's int thread id stays out of it. The generator and the factory produce the reshaped type; every existing caller keeps projecting to the string it already used, so nothing else changes behaviour. This is the prefactor that makes every later slice easy.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] Red-first: compose/parse tests beside the generator and key tests fail before the reshape, pass after.
- [x] Rendering an identity and parsing it back round-trips, including the non-forum case.
- [x] Parsing a satellite id, a correlation id, an eval-prefixed id, and a colon-free GUID each returns null — no throw, no coercion.
- [x] The conversation spelling is derived from the parts; the record stores no redundant copy that could disagree.
- [x] The browser client and the factory compile and their tests stay green with the reshaped type.
- [x] `dotnet test` (unit) green; no signature outside the conversations area changes yet.

## Comments

Implemented 2026-08-30 (872b7b1b). The reshaped type dropped `TopicId` from the record: Telegram
constructs the identity with no topic in existence, so the topic spelling lives with the callers
that mint topics (`ConversationIdGenerator.NewTopicId()` beside `CreateFor`). `Parse` is the exact
inverse of the render — a value whose canonical re-render differs (whitespace, signs, extra parts)
returns null.
