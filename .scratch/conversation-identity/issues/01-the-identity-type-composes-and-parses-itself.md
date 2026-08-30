# 01 — The identity type composes and parses itself

**What to build:** The existing domain conversation-identity record becomes the authored home of the identity, on the agent-key pattern: a readonly record struct whose conversation spelling is derived, not stored, with a total render and a partial inverse that returns null for anything that is not a conversation identity — a satellite id, a correlation id, an eval id, a bare GUID. The type owns the non-forum rule (an equal thread and chat means no thread); the narrowing to the Telegram API's int thread id stays out of it. The generator and the factory produce the reshaped type; every existing caller keeps projecting to the string it already used, so nothing else changes behaviour. This is the prefactor that makes every later slice easy.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Red-first: compose/parse tests beside the generator and key tests fail before the reshape, pass after.
- [ ] Rendering an identity and parsing it back round-trips, including the non-forum case.
- [ ] Parsing a satellite id, a correlation id, an eval-prefixed id, and a colon-free GUID each returns null — no throw, no coercion.
- [ ] The conversation spelling is derived from the parts; the record stores no redundant copy that could disagree.
- [ ] The browser client and the factory compile and their tests stay green with the reshaped type.
- [ ] `dotnet test` (unit) green; no signature outside the conversations area changes yet.
