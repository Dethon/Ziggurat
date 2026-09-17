# 05 — A live turn asks while it recalls

**What to build:** The head start. `ConversationGroup` starts the preloader where it builds the user message, concurrently with `IMemoryRecallHook.EnrichAsync`, and puts the pending result on the message; `SkillsProvider` takes a pending result when the message carries one and only otherwise asks for itself. The deadline runs from when the call started, not from when the provider looked. The dependency is nullable in `ConversationGroup` exactly as the recall hook is, and a chat command still builds nothing.

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] With a recall that takes 500 ms and a judgment that takes 400 ms, the user message is ready in about 500 ms, not 900 — asserted with `TimeProvider`, not a stopwatch.
- [ ] A message carrying a pending result causes no second judgment call in the provider.
- [ ] A judgment started at turn build that is still pending when the deadline (from its start) passes inserts nothing.
- [ ] A failing or throwing preloader never fails the message build; recall's result is unaffected.
- [ ] A `/clear` or `/cancel` starts no judgment.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § Where it happens.
