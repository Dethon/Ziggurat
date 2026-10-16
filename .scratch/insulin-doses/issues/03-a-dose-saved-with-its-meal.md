# 03 — A dose saved with its meal

**What to build:** When she saves a meal she can note the rapid insulin she took for it. The meal and
the dose are saved together or not at all, and the dose is linked to the meal and stamped with its
time. In the Log a meal shows the rapid units linked to it. A dose added or edited on its own can be
linked to one of her recent meals. Deleting a meal keeps the doses that were linked to it: the link
is cleared, and the confirmation says the doses stay.

**Blocked by:** 01, 02

**Status:** ready-for-agent

Spec: `.scratch/insulin-doses/spec.md`

- [ ] A meal create request carrying a dose stores both. The dose is `rapid`, has the meal's `eatenAt` and is linked to the meal.
- [ ] A meal create request carrying a refused dose stores neither, and the dose's sentence is returned.
- [ ] A meal update (the whole meal sent again) leaves its doses as they were.
- [ ] The meal list answers each meal with its linked doses' identifiers and units.
- [ ] Deleting a meal keeps its doses, with their meal link cleared.
- [ ] The meal screen's "Insulin taken" field starts empty on every new meal, is cleared on reset and is never prefilled from the carbs or a past dose (vitest on the draft).
- [ ] A meal row in the Log shows the total rapid units linked to it (vitest).
- [ ] The add and edit dose sheets offer the meals of roughly the last day to link to, and none.
- [ ] Deleting a meal with linked doses says in its confirmation that the doses are kept.
