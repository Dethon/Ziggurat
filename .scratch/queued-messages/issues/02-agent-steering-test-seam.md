# 02: An agent test seam over the real tool loop

**What to build:** A prefactor that adds the agent test seam the spec chose. It drives the chat monitor through the fake channel connection, as the monitor tests do. Its agent runs the real tool-loop approval client over a scripted model and a local tool, instead of a fake agent, so one test can observe both a conversation group's routing and what the tool loop does.

Use the seam to pin, from the library itself, how the function-invoking loop treats a request to terminate:
- in the concurrent path, which this client uses;
- in the sequential path;
- in particular, whether every call of a batch still has its result stored.

Steering (ticket 07) relies on that answer.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] A test sends a person's message through the fake channel connection, the scripted model calls the local tool, and the test observes the replies on the channel and the stored line (user message, call, result, final text).
- [ ] A test pins what terminating from inside a tool call does when a batch runs concurrently: every call's result is stored and no further model call is made. If the library behaves otherwise, the test states what it does instead.
- [ ] The same is pinned for a sequential batch.
- [ ] The seam runs without a network, a real model or Docker.
