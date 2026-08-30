# 03 — By-ref intent: action and back migrate, popup folds in

**What to build:** Element actions run through the new seam. `OnRefAsync(ref, work)` routes a ref to the tab that stamped it and answers the routing walls (no session, superseded-with-URL, closed-with-URL, unknown when the range aged out of the bounded registry). The action and back operations migrate onto it (back is ref-less on the acting tab — use the current-tab method from ticket 02 if it landed, otherwise route as today and reconcile when it does). Popup adoption folds inside the module: pending-popup pickup happens at the right pipeline moments, the popup's lock nests inside the acting tab's lock under the module-held no-cycle invariant (a popup is always a fresh page, never an existing tab), and the action's outcome says whether a popup answered — the browser's only remaining popup duty is registering the page's popup event. The supersede rule for these intents: iff the URL moved across the work — same-URL DOM churn under an action deliberately keeps refs alive so a form can be filled a field at a time (ADR-0034).

**Blocked by:** 01 — Pipeline + browse intent.

**Status:** done

- [x] `OnRefAsync` exists; action and back call the new methods and perform no session bookkeeping of their own
- [x] Each routing wall surfaces through the outcome union and maps to its existing action status in one switch
- [x] Ordering fact: a ref whose tab was evicted between routing and locking answers the closed wall (the under-lock re-check, previously hand-written per site)
- [x] Ordering fact: an action that moves the URL supersedes; one that does not leaves earlier ranges routing (red first — this unifies the two divergent conditionals)
- [x] Ordering fact: an action that opened a popup answers from the popup; no pending-popup pickup or popup locking remains in the browser
- [x] Ordering fact: one tab dying mid-action is answered as that tab's closed wall, never as the connection dying
- [x] Existing suites stay green; image fetch still drives the old verbs

## Comments

Implemented 2026-08-29. Notes:

- `OnRefAsync` has two shapes for the one intent: the plain work form (image fetch, ticket 04) and
  the action form (act / answer / `PopupAnswer<T>`), because popup pickup must land between the
  gesture and the read while both stay caller-supplied page choreography.
- The aged-out/never-issued ref keeps ADR-0034's decided third outcome by falling to the current
  tab *inside the module* (its DOM answers the ordinary not-found); the `Unknown` wall surfaces
  only when no current tab remains to fall to, and the action maps it to session-not-found exactly
  as the old `tab == null` path did.
- Back rode `OnCurrentTabAsync` directly (ticket 02 had landed), as the ticket anticipated.
- When a popup answers, the acting tab's tail (supersede/note/commit) is skipped on purpose —
  today's behaviour: the acting tab was never read, so nothing is stamped or noted there.

Review note (2026-08-29): the ticket body's "the browser's only remaining popup duty is
registering the page's popup event" was already stale when written — `Popup +=` registration
(`HandlePageEvents`) lived in `BrowserSessionManager` on master before this branch. The browser
has no popup duty at all; nothing needed changing for it.
