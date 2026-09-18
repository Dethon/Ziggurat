# 05 — A live turn asks while it recalls

**What to build:** The head start. `ConversationGroup` starts the preloader where it builds the user message, concurrently with `IMemoryRecallHook.EnrichAsync`, and puts the pending result on the message; `SkillsProvider` takes a pending result when the message carries one and only otherwise asks for itself. The deadline runs from when the call started, not from when the provider looked. The dependency is nullable in `ConversationGroup` exactly as the recall hook is, and a chat command still builds nothing.

**Blocked by:** 04

**Status:** resolved

- [x] With a recall that takes 500 ms and a judgment that takes 400 ms, the user message is ready in about 500 ms, not 900 — asserted with `TimeProvider`, not a stopwatch.
- [x] A message carrying a pending result causes no second judgment call in the provider.
- [x] A judgment started at turn build that is still pending when the deadline (from its start) passes inserts nothing.
- [x] A failing or throwing preloader never fails the message build; recall's result is unaffected.
- [x] A `/clear` or `/cancel` starts no judgment.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § Where it happens.

## Answer

Shipped 2026-09-18.

- `ConversationGroup` takes `ISkillPreloader?` (nullable beside the recall hook; `ChatMonitor` passes it through
  as a trailing optional parameter). In `BuildUserMessageAsync` it starts `PreloadAsync` before awaiting recall and
  attaches the task to the message through `SkillPreloadPending` (Domain, a `ConditionalWeakTable<ChatMessage,
  Task<SkillPreload>>` — beside the message rather than in its properties, because the message is persisted and a
  task is not a property; taken once). The task never faults: a throw is `Error` with no skills.
- What the group judges over comes from the agent that owns the session: `DisposableAgent.GetSkills(thread)` and
  `GetHistoryAsync(thread, ct)`, virtual and empty by default, overridden by `McpAgent` (`SkillsOf`, and
  `RedisChatMessageStore.ReadAsync`, which reads by the session's key and mints none). One extra history read per
  live turn, concurrent with recall's own — the price of asking exactly over the skills not yet loaded.
- `SkillsProvider` takes the pending result when the message carries one and only otherwise asks itself. The
  deadline runs inside `SkillPreloader` from when the judge is asked, so it runs from the build, not from the look.
- Commands never reach `BuildUserMessageAsync`, so `/clear` and `/cancel` start nothing.
- Tests: `ChatMonitorSkillPreloadTests` (6) — recall 500 ms and judgment 400 ms both armed on an `ArmedClock` before
  either ends, message ready after one 500 ms advance; a judgment past its deadline rides as `Deadline`; a throwing
  preloader never fails the build and recall's context is set; the second turn is judged against the persisted first
  turn; `/clear` and `/cancel` ask nothing. `SkillsProviderPendingTests` (5) — a pending result causes no second
  judgment, inserts what it says, and is taken once.
