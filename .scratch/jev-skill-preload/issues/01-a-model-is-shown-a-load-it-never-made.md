# 01 — A model is shown a load it never made

**What to build:** A spike, not a feature. Against each deployed model through the real wire (`deepseek/deepseek-v4.1-flash` and `openai/gpt-5.6-luna`, which the agents ship on; every `patchableModels` entry, the glm pair included, since a WebChat turn can be sent to any of them; and a Lemonade model if a box is reachable), send a conversation whose history holds an assistant `load_skill` function call the model never emitted, its function result carrying a real skill body wrapped as the framework wraps it, and a user request that needs that skill. No reasoning content on the fabricated message. Try the call-id shapes the repo already uses in tests and an `fc_`/`call_`-prefixed one. Record per model: accepted or refused (with the provider's error verbatim), whether the model then acted on the body without calling `load_skill` again, and whether a second turn in the same conversation still goes through with the pair in history. Throwaway code; nothing merges but the answer.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] Every deployed model has a verdict under `## Answer`, with the refusing provider's error quoted where there is one.
- [x] The call-id shape that every accepting model takes is named; ticket 04 uses it.
- [x] For each refusing host the fallback form (spec § What is inserted) is tried the same way and its verdict recorded.
- [x] The model that reloaded a skill it was shown as loaded, if any, is named — that is a `skills` section wording problem for ticket 04, not a wire problem.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § What is inserted.

## Answer

Run 2026-09-18 on the Responses wire through OpenRouter (`POST /api/v1/responses`, the wire
`OpenRouterChatClient` rides), `store: false`, no reasoning item on the fabricated call. The pair:
a `function_call` item (`name: load_skill`, `arguments: {"skillName": "countdown-timers"}`) and
its `function_call_output` carrying the body wrapped exactly as `AgentSkillsProvider` wraps a
load (`<name>…</name>\n<description>…</description>\n\n<instructions>\n…\n</instructions>\n\n<available_resources />\n\n<available_scripts />`,
captured off a real load on Microsoft.Agents.AI 1.20.0). Placed after the user request
("pon un temporizador de ocho minutos en la cocina"), as the spec inserts it. Second turn:
"¿cuánto queda?" with the pair and the model's own output items still in history.

| Model | `call-1` | `call_abc123` | `fc_abc123` | Reloaded? | Acted on body? | 2nd turn |
|---|---|---|---|---|---|---|
| `deepseek/deepseek-v4.1-flash` | accepted | accepted | accepted | no | yes (`vfs_text_create`) | ok |
| `openai/gpt-5.6-luna` | accepted | accepted | accepted | no | yes (`vfs_text_create`) | ok |
| `z-ai/glm-5.3` | accepted | accepted | accepted | no | yes (`vfs_text_create`) | ok |
| `z-ai/glm-5.3-flash` | accepted | accepted | accepted | no | yes (`vfs_text_create`) | ok |

**Verdict: no deployed host refuses the pair.** Every model, on every call-id shape, treated the
fabricated load as its own: it went straight to the skill's tool and never called `load_skill`
again, and the next turn went through with the pair in history. No model reloaded a skill it was
shown as loaded, so the `skills` section needs no wording change for the pair form.

**Call-id shape for ticket 04:** any of the three works everywhere; use the framework's own
`FunctionCallContent` with a plain unique id (the repo's `call-1` style is fine, no `fc_`/`call_`
prefix needed). The Responses adapter sends only `call_id`, never an `id`, and no provider
minded.

**The fallback form was still tried** (one `user` message carrying the same wrapped body, before
the request). Accepted everywhere, but weaker: `deepseek-v4.1-flash` and `gpt-5.6-luna` both
called `load_skill` again despite the body being in front of them; the glm pair acted on it
directly. So the fallback is worse than the pair on two of four hosts and is **not needed** —
ticket 04 uses the pair form on every host. The spec's `skills`-section sentence for the fallback
form is not written.

**Lemonade:** no box was reachable (`lemonade:13305` unresolvable from this machine). Moot for
the feature: the spec gates every Lemonade turn out of the preload (§ Where it happens), so no
Lemonade model is ever shown the pair.

**Not merged:** the spike script (throwaway, run from the scratchpad; a Python sibling of
`probe/jev_probe.py` against OpenRouter's Responses endpoint).
