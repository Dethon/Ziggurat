# 03 — The rule that turns answers into a preload

**What to build:** `ISkillPreloader` and `SkillPreloadPolicy` in Domain. The preloader takes the request text, the session's advertised skills and the conversation's history; drops the skills already loaded (a `load_skill` call naming them, whoever made it, or the fallback form from ticket 01); asks the typed-judgment contract the choice-plus-none and one noul per remaining skill, each judged by the skill's shipped description verbatim; and hands the answers to the policy. The policy is a pure function: a confident `none` vetoes, the choice winner at its bar, other skills at the noul bar added highest first, truncated to the cap. Settings `skillPreload: { enabled, deadlineMs, choiceConfidence, noulProbability, noneVeto, maxSkills }` in `Agent/appsettings.json` alone. No agent wiring yet — this ticket ends at a tested service.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The policy's four rules each have a test, including: winner under the bar with a noul over it preloads only the noul's skill; a vetoing `none` beats a noul over the bar; three qualifying skills are cut to two by probability.
- [ ] A skill already loaded is not in the question and cannot be in the result; with every skill loaded, or none advertised, no question is asked.
- [ ] The question instructions say "request" and never assume a person is speaking.
- [ ] The criteria text is `PromptSkill.Description` and nothing else, so an undeclared skill from an outpost is asked about like any other.
- [ ] An absent answer from the contract is an empty preload with its reason (`deadline` / `error`) carried for telemetry.
- [ ] `enabled: false` asks nothing.
- [ ] Spec: `.scratch/jev-skill-preload/spec.md` § What is asked, § The rule.
