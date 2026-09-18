# 09 — Four scorecards decide the merge

**What to build:** Nothing. Run the acceptance the spec sets: full tier, `ZIGGURAT_EVAL_EXHAUSTIVE=1`, on the shipped model and with `ZIGGURAT_EVAL_MODEL=openai/gpt-5.6-luna`, each once with `skillPreload.enabled` false and once true. Back up each scorecard out of `.eval-output/` before the next run starts; build nothing while a pass runs; check the scorecard's model field on the luna runs. This spends real money — it is the one ticket that does, and it was asked for.

**Blocked by:** 05, 06, 07, 08

**Status:** resolved

- [x] Four scorecards are kept and named by model and preload state.
- [x] On both models: claim pass rate not lower with the preload on; `SkillNotLoaded` not higher; no scenario red on a wrong preload; input tokens per scenario lower.
- [x] The loader split is reported: how often Jev preloaded, how often the model still loaded for itself, how often nobody did.
- [x] Known recurring reds (the warm-up probe tic) are discounted by the rules already written down, not by eye.
- [x] A failed bar is reported as failed, with the scenarios that moved, and nothing is merged on it.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § Acceptance.

## Answer

Run 2026-09-18 (UTC 00:59–01:32), full tier, `ZIGGURAT_EVAL_EXHAUSTIVE=1`, preload toggled with
`SKILLPRELOAD__ENABLED` (bound over the file through `ShippedDefinition`), luna with `ZIGGURAT_EVAL_MODEL`. Kept as
`.eval-output/scorecard-full.2026-09-18.{shipped,luna}.preload-{off,on}.json` (luna on: `...luna.preload-on-3.json`,
see below). The shipped model is `deepseek/deepseek-v4.1-flash` (Together); luna's scorecards say `openai/gpt-5.6-luna`
(OpenAI). Every "off" card says `"preload": "off"`, every "on" card `"preload": "jev-1.13.0"`.

| | shipped off | shipped on | luna off | luna on |
|---|---|---|---|---|
| claim pass rate | 97.41% (376/386) | **97.67%** (377/386) | 95.60% (369/386) | **95.85%** (370/386) |
| scenario rate | 97.44% (228/234) | 96.58% (226/234) | 97.01% (227/234) | 96.58% (226/234) |
| SkillNotLoaded | 0 | 0 | 0 | 0 |
| loader host / model / nobody | 0 / 194 / 0 | **160 / 34** / 0 | 0 / 194 / 0 | **74 / 120** / 0 |
| input tokens per run | 57,898 | **56,946** | 54,692 | **51,383** |
| model spend | $1.19 | $1.12 | — | $1.11 |
| Jev spend | — | $0.022 (225 calls, 490k tok) | — | $0.011 (109 calls, 237k tok) |
| judge outcomes | — | (field added after this run) | — | preloaded 80 · abstained 29 · deadline 125 |

**All four bars hold on both models:** the claim pass rate did not drop (it rose a claim on each), `SkillNotLoaded`
stayed at 0, input tokens per run fell, and no scenario was red on a wrong preload — every red run's dump was read:

- shipped on: "emptying a folder is asked about before anything goes" (2/3 → 1/3) and "research is paid for once,
  in place or delegated" (3/3 → 1/3). Both preloaded the *right* skill (vault, web-browsing) and failed on the call
  ceiling — the model read seven notes / browsed one page three times. The host's load counts toward the ceiling
  exactly as the model's own load always did, so nothing moved in the accounting; whether a body present up front
  makes deepseek more thorough is a question these N=3 policies cannot answer (the nine scenarios that moved are
  five up, four down).
- luna on: "a rename takes the links that pointed at it" (3/3 → 1/3) and "removing a watch deletes it from the
  home" (0/3 → 0/3): both `loader: model` — no preload was involved; the watch one was red with the preload off too.

**Two luna "on" passes were discarded**, kept as `...luna.preload-on.json` and `...-on-2.json`: both say
`"preload": "off"` with `loader.host = 0` — every one of their 234 judgments hit the 600 ms deadline (TypeSafe
answered 0.7–0.8 s per call from this network between 01:16 and 01:27, back to ~350 ms by 01:30). That is the
deadline doing its job, but it made the two cards indistinguishable from a pass with no key, which is why the
scorecard now also carries `summary.preloadOutcomes` (commit aa8fa0862). Even the good luna pass lost 125 of 234
judgments to the deadline: **the 600 ms deadline is marginal from this network under the eval's parallelism**
(4–8 stacks at once; the probe's 380 ms was one call at a time). In the deployment one turn at a time is the norm,
and a miss costs nothing but the head start; raising `skillPreload.deadlineMs` is the config edit to make if the
dashboard's deadline share says so.

Discounting: no run was red on the warm-up probe tic (`maxResults<=1`) in these passes. Both "off" baselines carried
reds of their own on luna (a heading rename 1/3, removing a watch 0/3) that pre-date this branch.

**After the review (same day):** the code review found that a group's first turn built its message before the
warmup that dials the servers, so the group-started judgment saw no skills and the provider did not ask for itself
— the eval never hit it because its runs go through the provider path. Fixed (`ConversationGroup.PreloadAsync`
awaits the warmup first; `OnAGroupsFirstTurn_TheJudgmentWaitsForTheWarmup…`). The eval path the four scorecards
measured is unchanged by that fix, so the cards stand; `"preload"` now reads `"unanswered"` rather than `"off"`
for a pass whose judgments all missed the deadline.
