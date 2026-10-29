# 11: Sugary per person

**What to build:** Meals, doses, pens, periods, day notes, exercise sessions and push subscriptions get a user id; foods, recipes and the pantry get the tenant id and stay shared (EF migration, provider-neutral). The access code and its settings go; the app and its API act for the forwarded user, and so do its channel turns. Jonas's `/sugary` mount and `fs_query` answer for the turn's person only. Spec: `../spec.md` ("Sugary").

**Blocked by:** 02, 06

**Status:** ready-for-agent

- [ ] One person's meals and doses are invisible to another, through the API and through `/sugary` (Sugary tests)
- [ ] A recipe one person adds is usable by another in the household
- [ ] A Sugary channel turn carries the signed-in person's principal
- [ ] The Sugary eval scenarios still pass on the harness (deterministic part)
