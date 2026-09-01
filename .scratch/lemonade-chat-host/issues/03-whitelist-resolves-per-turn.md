# 03 — The patchable whitelist resolves per turn from a source

**What to build:** When an agent checks a config patch's model against the patchable models it may honour, it asks a live source at that moment instead of a list captured when the agent was built. Today the only source is the configured patchable list, so a person switching models sees no change: an id in the list is honoured with the list's casing, an id outside it is refused with the existing warning and fallback. This is a prefactor: a later ticket adds discovered Lemonade ids to the source, and an id that appeared after the agent was built must still be honoured.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A patch naming a configured patchable model is honoured on a turn even if the agent was built before the source last changed
- [x] A patch naming a model the source no longer offers is refused with the existing warning and falls back to the agent's model
- [x] Subagents still offer no patchable models
- [x] The multi-agent factory and agent tests stay green, with any test that pinned the construction-time list moved to the source shape
