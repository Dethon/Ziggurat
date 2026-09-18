# 03 — The rule that turns answers into a preload

**What to build:** `ISkillPreloader` and `SkillPreloadPolicy` in Domain. The preloader takes the request text, the session's advertised skills and the conversation's history; drops the skills already loaded (a `load_skill` call naming them, whoever made it, or the fallback form from ticket 01); asks the typed-judgment contract the choice-plus-none and one noul per remaining skill, each judged by the skill's shipped description verbatim; and hands the answers to the policy. The policy is a pure function: a confident `none` vetoes, the choice winner at its bar, other skills at the noul bar added highest first, truncated to the cap. Settings `skillPreload: { enabled, deadlineMs, choiceConfidence, noulProbability, noneVeto, maxSkills }` in `Agent/appsettings.json` alone. No agent wiring yet — this ticket ends at a tested service.

**Blocked by:** 02

**Status:** resolved

- [x] The policy's four rules each have a test, including: winner under the bar with a noul over it preloads only the noul's skill; a vetoing `none` beats a noul over the bar; three qualifying skills are cut to two by probability.
- [x] A skill already loaded is not in the question and cannot be in the result; with every skill loaded, or none advertised, no question is asked.
- [x] The question instructions say "request" and never assume a person is speaking.
- [x] The criteria text is `PromptSkill.Description` and nothing else, so an undeclared skill from an outpost is asked about like any other.
- [x] An absent answer from the contract is an empty preload with its reason (`deadline` / `error`) carried for telemetry.
- [x] `enabled: false` asks nothing.
- [x] Spec: `.scratch/jev-skill-preload/spec.md` § What is asked, § The rule.

## Answer

Shipped 2026-09-18 in `Domain/Skills/`:

- `SkillLoadTool` — Domain's spelling of the load (`load_skill`, `skillName`), pinned to the framework's by
  `McpAgentSkillsTests.TheDomainsSpellingOfTheLoad_IsTheFrameworks`; `LoadedIn(history)` reads every
  `FunctionCallContent` naming the tool, whoever made it; `AsLoaded(skills, callIdPrefix)` writes the pair form
  ticket 01 settled (one assistant message with a call per skill, one tool message with the results), and
  `Wrapped(skill)` is the framework's wrapper, pinned against a real load by
  `ALoad_ReturnsTheBodyWrappedExactlyAsTheDomainWritesAPreload`. No fallback form: ticket 01 found no host needs it.
- `SkillPreloadPolicy.Decide(choice, needs, settings)` — the four rules, pure; nine tests.
- `SkillPreloader(IJudge, SkillPreloadSettings, TimeProvider) : ISkillPreloader` — drops the loaded skills, asks
  `skill` (choice over `PromptSkill.Description` verbatim + `none`) and `needs_<name>` (one noul each), runs the
  deadline from the call on a `CancellationTokenSource(delay, timeProvider)`, maps absence to `Deadline`/`Error`,
  `Unconfigured` to `NotAsked`, a `lemonade/` config-patch model to `SkippedLemonade`. Fifteen tests.
- `SkillPreloadSettings` bound from `skillPreload` in `Agent/appsettings.json` (`enabled`, `deadlineMs: 600`, the
  three bars at 0.9, `maxSkills: 2`).

Wording: the framing is "`request` is a request made to an assistant, in Spanish or English." — the probe
said "what a person just said to a home assistant"; ticket 08 re-measures under the shipped wording.
