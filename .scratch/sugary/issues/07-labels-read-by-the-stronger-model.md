# 07 — Labels read by the stronger model

**What to build:** She photographs a nutrition label and its table is read into carbs per 100 g and serving size. A label turn asks for the label's response format and carries an agent config patch naming the label model (Opus 5.5 today), which is one of the `sugary` agent's patchable models. A photo that is not a readable label is refused rather than guessed.

**Blocked by:** 05

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] A label turn's request uses the label model and the label schema; a meal turn is unaffected.
- [ ] The label model is in the agent's patchable models; a model outside the list is never honoured.
- [ ] A non-label photo comes back as unreadable and the app says so.
- [ ] The read label can be saved to her foods with source `label`.
