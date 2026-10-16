# 05 — Jonas reads her doses

**What to build:** When she or anyone asks Jonas about her insulin ("how much did she take for the
pizza", "what was her basal last week"), the `sugary-log` skill sends him to the dose tables. It says
how to find a meal's doses and how to line them up with the meal and the glucose under `/ha`. It also
says the doses are her record to read, never a basis for suggesting a dose, ratio, correction or
timing.

**Blocked by:** 03, 04

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [ ] The skill body names `/sugary/doses/<YYYY-MM>.csv`, how to find a meal's doses (text search for its `meal_id` under `/sugary/doses`), and the never-advise rule.
- [ ] The skill description is widened to cover questions about her insulin doses, and stays within the skill budget.
- [ ] Prompt staleness and budget tests pass, and the prompt snapshots are updated.
- [ ] An armed changed-tier eval run against `master` is taken after the edit and its scorecard checked. Any red is either explained or fixed.
- [ ] The `sugary` agent's definition and the estimating prompt are unchanged.
