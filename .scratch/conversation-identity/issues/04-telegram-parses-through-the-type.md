# 04 — Telegram parses through the type

**What to build:** The Telegram channel server's two byte-identical hand-rolled parsers die; both tools parse the conversation identity through the type's inverse. The non-forum rule they privately agreed on is now the type's rule, and the narrowing to the Telegram API's int thread id happens in the Telegram projection. A non-conforming id — which an unguarded parse used to throw on — now produces a loud, named error result rather than an exception, since a Telegram tool should never receive another channel's address.

**Blocked by:** 01 — The identity type composes and parses itself.

**Status:** resolved

- [x] Red-first: a tool test hands a non-conforming id and asserts the named error instead of the exception it gets today.
- [x] Both tools parse via the type; no split-on-colon parsing remains in the Telegram server.
- [x] The non-forum case (equal thread and chat) still sends without a thread — existing tests green.
- [x] The inbound composition site (Telegram message to conversation identity) constructs the type rather than interpolating.
- [x] All existing Telegram tool tests pass, reworded only where they asserted the old exception.
