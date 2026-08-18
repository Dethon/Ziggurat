# 02 — First scenario end to end

**What to build:** the first **scenario**. A voice turn asking for an eight-minute pasta timer,
run against a real model, creates the timer at the expected path with the expected JSON — and
the test says so.

The agent comes from the real factory built on the shipped agent definition, so the model,
reasoning effort, provider routing, language and prompt sections are the ones that ship; only
the configured MCP endpoint urls are rewritten to the in-process server. The timers server runs
in-process over a stubbed voice hub. One pinned instant is shared by the agent and the server.
The turn reaches the model decorated the way a channel decorates it — sender, room, satellite and
timestamp — because the speaking-room rule is only decidable from that.

The suite is opt-in: a bare test run must not touch it even when an OpenRouter key is present.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] A scenario declares a turn (sender, room, satellite, timestamp), a pinned instant, and the
      calls it requires with argument and path matchers.
- [ ] Running it creates the timer, and the assertion reads the recording rather than the
      server's storage.
- [ ] The agent is built through the real factory from the shipped definition; no wiring is
      reimplemented in the harness.
- [ ] The eval category does not run on a bare test invocation with a key present; running it
      requires an explicit filter.
- [ ] The project instructions' "just run the tests" line records that the eval category is
      excluded and why.
- [ ] The scenario is demonstrated red once by deleting the prose it relies on, noted in the
      commit.
