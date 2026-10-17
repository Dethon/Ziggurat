# 04 — Jonas reads her exercise

**What to build:** When she or anyone asks Jonas about her exercise ("did she walk after the paella",
"was the low the night after the gym"), the `sugary-log` skill sends him to the exercise tables, to
read beside the glucose under `/ha`, and says they are her record, never a basis for suggesting
carbs, a dose or a timing around exercise.

**Blocked by:** 03

**Status:** ready-for-agent

Spec: `.scratch/exercise/spec.md`

- [ ] The skill body names `/sugary/exercise/<YYYY-MM>.csv` and that a running session has no end yet, and the never-advise rule covers exercise.
- [ ] The skill description is widened to cover her exercise, and stays within the skill budget; the body budget is raised to fit, said in the manifest's comment.
- [ ] Prompt staleness and budget tests pass, and the prompt snapshots are updated.
- [ ] An armed changed-tier eval run against `master` is taken after the edit, once the user agrees to the spend, and its scorecard checked.
- [ ] The `sugary` agent's definition and the estimating prompt are unchanged.
