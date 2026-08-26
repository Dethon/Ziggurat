# 05 — A popup becomes a tab

Status: ready-for-agent
Blocked by: 03

**What to build:** A page opened by the site itself — `target=_blank`, `window.open` — is adopted
into the session's tab pool instead of leaking. It counts against the cap (evicting under it),
becomes the current tab, and the action that spawned it answers from it: snapshot and content of
where the site actually took the model, not a stale diff of the page left behind. A popup nothing
asked for and nothing keeps touching ages out through the same LRU as any tab.

- [ ] An action whose click opens a new tab answers from the popup — its URL, its content, its refs
- [ ] The popup joins the pool: it is current, it counts against the cap, and adopting it can evict
- [ ] The popup's refs are stamped session-unique like any tab's
- [ ] No page created by the site during a session outlives the session unowned
- [ ] Covered at the integration seam (a real `target=_blank` click) with the adoption policy asserted at the session-management unit seam
