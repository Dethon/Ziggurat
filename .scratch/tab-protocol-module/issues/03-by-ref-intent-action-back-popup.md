# 03 — By-ref intent: action and back migrate, popup folds in

**What to build:** Element actions run through the new seam. `OnRefAsync(ref, work)` routes a ref to the tab that stamped it and answers the routing walls (no session, superseded-with-URL, closed-with-URL, unknown when the range aged out of the bounded registry). The action and back operations migrate onto it (back is ref-less on the acting tab — use the current-tab method from ticket 02 if it landed, otherwise route as today and reconcile when it does). Popup adoption folds inside the module: pending-popup pickup happens at the right pipeline moments, the popup's lock nests inside the acting tab's lock under the module-held no-cycle invariant (a popup is always a fresh page, never an existing tab), and the action's outcome says whether a popup answered — the browser's only remaining popup duty is registering the page's popup event. The supersede rule for these intents: iff the URL moved across the work — same-URL DOM churn under an action deliberately keeps refs alive so a form can be filled a field at a time (ADR-0034).

**Blocked by:** 01 — Pipeline + browse intent.

**Status:** ready-for-agent

- [ ] `OnRefAsync` exists; action and back call the new methods and perform no session bookkeeping of their own
- [ ] Each routing wall surfaces through the outcome union and maps to its existing action status in one switch
- [ ] Ordering fact: a ref whose tab was evicted between routing and locking answers the closed wall (the under-lock re-check, previously hand-written per site)
- [ ] Ordering fact: an action that moves the URL supersedes; one that does not leaves earlier ranges routing (red first — this unifies the two divergent conditionals)
- [ ] Ordering fact: an action that opened a popup answers from the popup; no pending-popup pickup or popup locking remains in the browser
- [ ] Ordering fact: one tab dying mid-action is answered as that tab's closed wall, never as the connection dying
- [ ] Existing suites stay green; image fetch still drives the old verbs
