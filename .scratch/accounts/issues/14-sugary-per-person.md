# 14: Sugary per person

**What to build:** Each person has their own Sugary log while the household shares its kitchen. The proxy checks the session in front of Sugary and the access code goes; meals, doses, pens, periods, day notes, exercise sessions and push subscriptions belong to a user, foods, recipes and the pantry to the household; the app, its API and its channel turns act for the signed-in person; Jonas's Sugary mount and query tool answer for the turn's person only. Spec: `../spec.md` ("Sugary").

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] One person's meals and doses are invisible to another, through the app's API and through Jonas's mount
- [ ] A recipe one person adds is usable by another in the household
- [ ] A Sugary channel turn carries the signed-in person's account
- [ ] The Sugary eval scenarios' deterministic checks still pass
