# 08 — Eval tier: the sugary agent on the benchmark meals

**What to build:** A new eval tier, run per model by name like the Research tier and never by a bare test run, scores the `sugary` agent on the standalone benchmark: 64 photographed meals from three public datasets with known carbs. Beside it, graded scenarios check that the agent never suggests a dose, asks about amounts first and does not relist already-counted foods. See the spec's "Testing Decisions".

**Blocked by:** 05

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The tier is selected only when asked for, with a model named, and spends nothing on a routine run.
- [ ] The scorecard gives mean absolute error, bias, how often the truth is inside the range, estimated against true weight, and time and cost per estimate, per set and overall.
- [ ] The case files and the reasons behind their truth values (fibre subtracted, self-reported sets) travel with the tier; the photos stay out of git under their licences.
- [ ] The graded scenarios pass on the agent's model.
- [ ] A first run's scorecard is compared with the standalone figures (about 13–14 g mean error from a photo) and the comparison is written down.
