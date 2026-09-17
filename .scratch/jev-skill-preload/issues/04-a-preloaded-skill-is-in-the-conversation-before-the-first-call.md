# 04 — A preloaded skill is in the conversation before the model's first call

**What to build:** The insertion, end to end through the agent. `SkillsProvider` asks the preloader for the turn's request, waits no longer than the deadline, and returns the preload as `AIContext.Messages` in the form ticket 01 settled for the turn's host — the synthetic `load_skill` call and its wrapped result, no reasoning content, or the fallback message. The history provider persists it, so the next turn's already-loaded check finds it. A Lemonade turn asks nothing, failing closed as recall's gate does. This path is the whole feature for a subagent and an eval run; ticket 05 only makes it free for a live turn. `.claude/rules/prompts.md` and the `skills` section say what is now true: a skill may already be in the conversation when the turn starts, and one that is, is loaded. Prompt snapshots regenerated and read.

**Blocked by:** 01, 03

**Status:** ready-for-agent

- [ ] Through a real `McpAgent` with a scripted chat client and a fake judgment contract: the first request the model receives already holds the body, after the user message, and the model's instructions and tools are byte-identical to a turn with the preload off.
- [ ] The inserted messages are in the persisted thread after the turn, and a second turn asks Jev about one skill fewer and inserts nothing twice.
- [ ] Two preloaded skills arrive as one assistant message with two calls and their two results.
- [ ] A judgment slower than the deadline inserts nothing and the turn proceeds; the late answer is discarded, never applied to a later turn.
- [ ] A turn whose config patch names a `lemonade/` model makes no judgment call.
- [ ] A subagent run gets a preload by the same path, judged on its delegation prompt.
- [ ] `load_skill` is still offered and still works; a model-made load after an abstention behaves exactly as today.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § Where it happens, § What is inserted.
