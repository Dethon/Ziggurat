# 06 — The eval knows who loaded a skill

**What to build:** The eval's half of the ADR 0039 refinement. A preload reaches `Recording` through an observation of its own, marked as the host's — it cannot come through `ToolApprovalChatClient`, which only sees calls the model emitted. `CallExpectation.LoadsSkill` is met by either loader; a preloaded skill outside the scenario's permitted set reddens it as a wrong load does. The scorecard carries per scenario and per pass who loaded (host / model / nobody), Jev's tokens and cost inside `spend`, and the Jev model beside the agent model. The TypeSafe key is read from user secrets beside the OpenRouter key; without one the pass runs with the preload off and the scorecard says so. The harness parts are deterministic and run on a bare `dotnet test`. `CLAUDE.md`'s eval paragraph and `.claude/rules/prompts.md` are updated: a trigger claim is now "a request of this kind gets the skill loaded", and its demonstrated red is still the description deleted — which now blinds both readers.

**Blocked by:** 04

**Status:** resolved

- [x] A scripted run with a host preload and no model load passes a `LoadsSkill` expectation and records the loader as host; the reverse records model; neither records `SkillNotLoaded`.
- [x] A scripted run preloading a skill the scenario does not permit is red, and the failure names the preload.
- [x] The scorecard's per-claim and per-scenario output carries the loader split, and `spend` includes Jev's input tokens at the configured price.
- [x] With no TypeSafe key the armed pass runs, no judgment call is made, and the scorecard carries `preload: off`.
- [x] The failure dump shows the inserted messages where they sat in the conversation.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § The eval.

## Answer

Shipped 2026-09-18.

- `Recording.Publish` takes the `SkillPreloadEvent` the preloader publishes (the recording is the stack's
  `IMetricsPublisher`) and turns each preloaded skill into a `ToolInvocation` of `load_skill` sequenced below zero,
  with `Recording.PreloadedResult` where the body would be. So the existing checks see it: `LoadsSkill` is met by
  either loader, an unpermitted preload is an "unnecessary call … (preloaded by the host)", `KindOf` stays
  `SkillNotLoaded` only when nobody loaded, and the dump lists it first, marked "preloaded by the host, before the
  model's first call".
- `ScenarioChecks.LoaderOf` → `Loader.{None, Host, Model, Nobody}`; `RunReading.Loader`, `ScenarioResult.Loaders`,
  `ClaimOutcome`/`ScenarioOutcome.Loaders`; the scorecard writes `"loader": {host, model, nobody}` per claim row,
  per scenario row and in the summary.
- `Spend` gains `PreloadCost/PreloadInputTokens/PreloadRequests`, spelled under `spend.preload` where any judgment
  was paid for; priced by `JevPrice` (`ZIGGURAT_EVAL_JEV_PRICE_PER_MTOK`, default $0.045/M from the probe's
  "a hundredth of a cent for 2.2k tokens" — TypeSafe answers tokens, never cost).
- The scorecard's top level carries `"preload": "<jev model>"` read off the events, or `"off"` when none were seen
  — a pass with no `typeSafe:apiKey` in user secrets (bound through `ShippedDefinition`, same secrets id as the
  agent) registers the unconfigured judge, asks nothing and is labelled so.
- Tests: `PreloadChecksTests` (8) and two `ScorecardTests`; 177 harness tests green on a bare run.
- Docs: `CLAUDE.md` eval paragraph; `.claude/rules/prompts.md` (a trigger claim gets the skill loaded; the
  description-deleted red blinds both readers).
- The "no key → preload: off" and "armed pass preloads" acceptance is exercised for real by ticket 09's runs.
