# 05 — Jonas reads her doses

**What to build:** When she or anyone asks Jonas about her insulin ("how much did she take for the
pizza", "what was her basal last week"), the `sugary-log` skill sends him to the dose tables. It says
how to find a meal's doses and how to line them up with the meal and the glucose under `/ha`. It also
says the doses are her record to read, never a basis for suggesting a dose, ratio, correction or
timing.

**Blocked by:** 03, 04

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [x] The skill body names `/sugary/doses/<YYYY-MM>.csv`, how to find a meal's doses (text search for its `meal_id` under `/sugary/doses`), and the never-advise rule.
- [x] The skill description is widened to cover questions about her insulin doses, and stays within the skill budget.
- [x] Prompt staleness and budget tests pass, and the prompt snapshots are updated.
- [x] An armed changed-tier eval run against `master` is taken after the edit and its scorecard checked. Any red is either explained or fixed.
- [x] The `sugary` agent's definition and the estimating prompt are unchanged.

## Comments

**2026-10-16 — changed-tier eval against `master`** (`ZIGGURAT_EVAL=1 ZIGGURAT_EVAL_CHANGED=1`, default
models, preload on): 4 scenarios selected, 8/8 runs green, $0.067. "A question about her log loads
the sugary-log skill before reading it" passed 2/2 with the skill preloaded by the host both times
(`loader`: host 2, model 0), so the widened description still selects it for the paella question.
The three sugary-agent scenarios, including "a meal sent with a request for a dose gets an estimate
and no dose", stayed green. The skill's never-advise rule is not cited by any claim or scenario,
because the spec asked for no new scenario.
