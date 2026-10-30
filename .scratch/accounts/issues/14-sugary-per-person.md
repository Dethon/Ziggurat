# 14: Sugary per person

**What to build:** Each person has their own Sugary log while the household shares its kitchen. The proxy checks the session in front of Sugary and the access code goes; meals, doses, pens, periods, day notes, exercise sessions and push subscriptions belong to a user, foods, recipes and the pantry to the household; the app, its API and its channel turns act for the signed-in person; Jonas's Sugary mount and query tool answer for the turn's person only. Spec: `../spec.md` ("Sugary").

**Blocked by:** 06

**Status:** done

- [x] One person's meals and doses are invisible to another, through the app's API and through Jonas's mount
- [x] A recipe one person adds is usable by another in the household
- [x] A Sugary channel turn carries the signed-in person's account
- [x] The Sugary eval scenarios' deterministic checks still pass

Notes for later tickets (`.claude/rules/sugary.md` has the detail):

- **16 (migration).** Every Sugary row from before this ticket, and every row the `import` verb brings, carries `legacy` (`Owners.Unassigned`) in each owner column and is seen by nobody. The command gives them to the named person: on `Meals`, `Doses`, `Pens`, `PushSubscriptions`, `ExerciseSessions`, `Periods`, `DayNotes` set `UserId` and `TenantId` where `UserId = 'legacy'`; on `Foods` and `Recipes` set `TenantId` where it is `legacy`. Running it twice changes nothing, since a second run finds no `legacy` row. `SugaryServer.GiveUnassignedRowsToAsync` is the same step as the suite does it. Sugary has no "people" or channel names to merge: the log was one person's.
- A Sugary turn's principal is `sugary` / the account's id / the account, and its sender is the account's id (no resolution call). 11's review-copy ownership reads that principal.
- `/sugary` refuses (`permission_denied`) a call whose principal names no account — a voice guest (08), a fire with no person behind it, a Telegram turn until 07 stamps the account. Jonas reads a person's log only on a turn that carries their account.
- `EvalRun.Turn` now stamps every turn's principal with `EvalRun.Account` (`eval-household` / `eval-user`), a person's own turn included, and the eval's Sugary log is seeded under it.
