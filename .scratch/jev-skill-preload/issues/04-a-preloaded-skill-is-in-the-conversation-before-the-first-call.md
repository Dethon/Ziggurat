# 04 — A preloaded skill is in the conversation before the model's first call

**What to build:** The insertion, end to end through the agent. `SkillsProvider` asks the preloader for the turn's request, waits no longer than the deadline, and returns the preload as `AIContext.Messages` in the form ticket 01 settled for the turn's host — the synthetic `load_skill` call and its wrapped result, no reasoning content, or the fallback message. The history provider persists it, so the next turn's already-loaded check finds it. A Lemonade turn asks nothing, failing closed as recall's gate does. This path is the whole feature for a subagent and an eval run; ticket 05 only makes it free for a live turn. `.claude/rules/prompts.md` and the `skills` section say what is now true: a skill may already be in the conversation when the turn starts, and one that is, is loaded. Prompt snapshots regenerated and read.

**Blocked by:** 01, 03

**Status:** resolved

- [x] Through a real `McpAgent` with a scripted chat client and a fake judgment contract: the first request the model receives already holds the body, after the user message, and the model's instructions and tools are byte-identical to a turn with the preload off.
- [x] The inserted messages are in the persisted thread after the turn, and a second turn asks Jev about one skill fewer and inserts nothing twice.
- [x] Two preloaded skills arrive as one assistant message with two calls and their two results.
- [x] A judgment slower than the deadline inserts nothing and the turn proceeds; the late answer is discarded, never applied to a later turn.
- [x] A turn whose config patch names a `lemonade/` model makes no judgment call.
- [x] A subagent run gets a preload by the same path, judged on its delegation prompt.
- [x] `load_skill` is still offered and still works; a model-made load after an abstention behaves exactly as today.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § Where it happens, § What is inserted.

## Answer

Shipped 2026-09-18. The pair form everywhere (ticket 01 found no host that needs the fallback).

- `SkillsProvider` (Infrastructure) takes an optional `ISkillPreloader` and a history reader. On each turn it
  takes the last user message the caller handed in (`InvokingContext.AIContext.Messages`), asks the preloader on
  its text with the session's skills and the history, and returns `SkillLoadTool.AsLoaded(...)` as
  `AIContext.Messages`: one assistant message with a `FunctionCallContent` per skill, one tool message with the
  wrapped results, no reasoning content. The framework places them after the user message; the history provider
  persists them with the turn.
- **Found while testing**: the framework hands a context provider only the caller's messages, not the history,
  so "already loaded" was blind on the second turn and re-inserted the pair. `RedisChatMessageStore` now memoises
  what it provided per session (`LastProvided`, a `ConditionalWeakTable`) and `McpAgent` hands that reader to the
  provider — the thread is read once per turn, as before.
- **Found while testing**: a judge answering after the deadline had its answer applied. `SkillPreloader` now
  discards any outcome once its own deadline token is cancelled, whatever the judge made of the cancellation.
- `McpAgent` takes `ISkillPreloader?`; `MultiAgentFactory` resolves it from DI at its one construction site, so a
  worker gets it too, judged on its delegation prompt. `InjectorModule` registers `SkillPreloader` over `IJudge`
  and `skillPreload`.
- The `skills` section gained "— by your load, or already there when your turn starts —" (245/250 tokens);
  snapshots regenerated and read: the three agents grew by 14 tokens each, nothing else moved.
  `.claude/rules/prompts.md` names the preload and the two-reader rule.
- Tests: `McpAgentSkillPreloadTests` (9) through a real `McpAgent` and MCP server with a scripted chat client and
  fake judge — pair after the user message with instructions and tools byte-identical to a preload-off turn;
  persisted and not inserted twice; two skills as one assistant message; deadline miss inserts nothing and the
  late answer never reaches a later turn; a `lemonade/` patch asks nothing; a worker is judged on its prompt;
  after an abstention a model-made load works as today; error and unconfigured leave the turn as today.
