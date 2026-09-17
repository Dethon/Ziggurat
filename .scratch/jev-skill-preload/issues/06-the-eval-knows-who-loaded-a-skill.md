# 06 — The eval knows who loaded a skill

**What to build:** The eval's half of the ADR 0039 refinement. A preload reaches `Recording` through an observation of its own, marked as the host's — it cannot come through `ToolApprovalChatClient`, which only sees calls the model emitted. `CallExpectation.LoadsSkill` is met by either loader; a preloaded skill outside the scenario's permitted set reddens it as a wrong load does. The scorecard carries per scenario and per pass who loaded (host / model / nobody), Jev's tokens and cost inside `spend`, and the Jev model beside the agent model. The TypeSafe key is read from user secrets beside the OpenRouter key; without one the pass runs with the preload off and the scorecard says so. The harness parts are deterministic and run on a bare `dotnet test`. `CLAUDE.md`'s eval paragraph and `.claude/rules/prompts.md` are updated: a trigger claim is now "a request of this kind gets the skill loaded", and its demonstrated red is still the description deleted — which now blinds both readers.

**Blocked by:** 04

**Status:** ready-for-agent

- [ ] A scripted run with a host preload and no model load passes a `LoadsSkill` expectation and records the loader as host; the reverse records model; neither records `SkillNotLoaded`.
- [ ] A scripted run preloading a skill the scenario does not permit is red, and the failure names the preload.
- [ ] The scorecard's per-claim and per-scenario output carries the loader split, and `spend` includes Jev's input tokens at the configured price.
- [ ] With no TypeSafe key the armed pass runs, no judgment call is made, and the scorecard carries `preload: off`.
- [ ] The failure dump shows the inserted messages where they sat in the conversation.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § The eval.
