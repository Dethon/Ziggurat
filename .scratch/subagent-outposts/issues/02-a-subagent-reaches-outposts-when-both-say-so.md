# 02 — A subagent reaches outposts when both say so

**What to build:** The payoff. Run an outpost on your machine, ask an agent that is allowed to see
it to do something big enough that it delegates, and the worker it spawns can read your files.
Turn the setting off on the worker, or off on the agent, and the worker cannot — the machine is
reached by delegation only when somebody said so twice.

What a subagent inherits is the opt-in, never a set of machines. Its own session build asks the
registry, so it mounts whatever is live at the moment it is spawned. A machine that registered
after the parent's session was built is there; one that has since expired is not; one that is
asleep costs its own mount and nothing else, exactly as ADR-0027 says. An inherited outpost is the
whole mount, command execution included where the machine was started allowing it.

The parent is the ceiling. A subagent whose own definition asks for outposts gets none if its
parent has none, so delegating can never reach somewhere asking directly could not.

**Blocked by:** 01 — A spawn carries the parent's context as one value.

**Status:** done

- [x] An outposts opt-in on the subagent definition, named as it is on an agent definition,
      defaulting to false and binding from the subagent profiles in settings.
- [x] The projection's subagent entry point computes the effective opt-in as the conjunction of the
      parent's and the definition's, replacing the hardcoded false and the comment justifying it.
      The comment's concern is answered by the definition's own flag, not dropped.
- [x] The shipped worker profile turns the opt-in on. Both agents that enable the subagents feature
      are already opted in, so this is live for chat and for voice on one edit.
- [x] Unit tests at the projection: both true yields an opted-in spec; parent true and definition
      false, parent false and definition true, and both false all yield an opted-out one.
- [x] A settings-binding test that the worker profile's opt-in is bound from configuration and that
      an omitted one is false.
- [x] An integration test that a subagent spawned from an opted-in parent has a live outpost among
      its mounts, driven through the existing outpost mounting fixture.
- [x] No change to endpoint composition, the dial policy or filesystem discovery. A subagent's
      session build asks the registry through the same path an agent's does, configured endpoints
      still come first, and a colliding name is still shadowed.
- [x] Red first, in that order: the projection tests fail on the hardcoded false before the rule
      exists, and the integration test fails on an absent mount before the profile is turned on.
