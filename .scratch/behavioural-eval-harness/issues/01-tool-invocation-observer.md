# 01 — Tool invocation observer and recording

**What to build:** any agent turn can be observed as an ordered **recording** — every tool
invocation with its name, raw arguments, result and any exception, in sequence — without changing
what a deployment does. A call whose tool threw, and a call naming a tool that does not exist,
both appear in the recording rather than vanishing.

The observer is the one new production seam (ADR-0030): the approval chat client already
intercepts every function invocation and holds all four values at that moment, so it gains an
optional observer resolved from dependency injection, absent by default and no-op wherever
nothing registers one.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] An agent run with no observer registered behaves exactly as it does today, including tool
      approval and the whitelist.
- [x] With an observer registered, every tool invocation of a turn is recorded once, in
      invocation order, with tool name and raw argument JSON.
- [x] A tool that throws is recorded with its error; the recording does not stop at it.
- [x] A call naming a tool that does not exist is recorded.
- [x] Concurrent invocations are all recorded, each with a distinct sequence position.
- [x] Every criterion above is proven with a scripted chat client replaying canned tool-call
      sequences — deterministic, no network, no model.
