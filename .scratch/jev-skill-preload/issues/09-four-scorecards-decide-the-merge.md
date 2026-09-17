# 09 — Four scorecards decide the merge

**What to build:** Nothing. Run the acceptance the spec sets: full tier, `ZIGGURAT_EVAL_EXHAUSTIVE=1`, on the shipped model and with `ZIGGURAT_EVAL_MODEL=openai/gpt-5.6-luna`, each once with `skillPreload.enabled` false and once true. Back up each scorecard out of `.eval-output/` before the next run starts; build nothing while a pass runs; check the scorecard's model field on the luna runs. This spends real money — it is the one ticket that does, and it was asked for.

**Blocked by:** 05, 06, 07, 08

**Status:** ready-for-agent

- [ ] Four scorecards are kept and named by model and preload state.
- [ ] On both models: claim pass rate not lower with the preload on; `SkillNotLoaded` not higher; no scenario red on a wrong preload; input tokens per scenario lower.
- [ ] The loader split is reported: how often Jev preloaded, how often the model still loaded for itself, how often nobody did.
- [ ] Known recurring reds (the warm-up probe tic) are discounted by the rules already written down, not by eye.
- [ ] A failed bar is reported as failed, with the scenarios that moved, and nothing is merged on it.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § Acceptance.
