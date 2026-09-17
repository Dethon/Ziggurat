# 01 — A model is shown a load it never made

**What to build:** A spike, not a feature. Against each deployed model through the real wire (`deepseek/deepseek-v4.1-flash` and `openai/gpt-5.6-luna`, which the agents ship on; every `patchableModels` entry, the glm pair included, since a WebChat turn can be sent to any of them; and a Lemonade model if a box is reachable), send a conversation whose history holds an assistant `load_skill` function call the model never emitted, its function result carrying a real skill body wrapped as the framework wraps it, and a user request that needs that skill. No reasoning content on the fabricated message. Try the call-id shapes the repo already uses in tests and an `fc_`/`call_`-prefixed one. Record per model: accepted or refused (with the provider's error verbatim), whether the model then acted on the body without calling `load_skill` again, and whether a second turn in the same conversation still goes through with the pair in history. Throwaway code; nothing merges but the answer.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Every deployed model has a verdict under `## Answer`, with the refusing provider's error quoted where there is one.
- [ ] The call-id shape that every accepting model takes is named; ticket 04 uses it.
- [ ] For each refusing host the fallback form (spec § What is inserted) is tried the same way and its verdict recorded.
- [ ] The model that reloaded a skill it was shown as loaded, if any, is named — that is a `skills` section wording problem for ticket 04, not a wire problem.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § What is inserted.
