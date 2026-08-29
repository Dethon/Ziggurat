# 01 — Pipeline + browse intent

**What to build:** Browsing a URL runs through the tab protocol's new seam. `BrowserSessionManager` gains its first per-intent public method, `BrowseAsync(url, work)`, backed by the one private pipeline that owns the whole ordering: acquire (with the bounded retry), lock, re-check the page is open under the lock, touch, read the URL before the work, run the work, decide supersede (browse: always — a same-URL re-browse reloads the DOM), note the final URL, open/commit the element-stamp lease around the work (the work sees the stamp start through the module-owned tab-work context and reports the ref count back), re-check closed after the work and override with the closed wall, and disambiguate a dead tab from a dead connection. The method returns the outcome union: ran-with-result or a named wall (no session, closed, exhaustion — retryable, no longer a control-flow exception). The navigate operation in the Playwright browser migrates onto it; the old verbs stay public untouched for the not-yet-migrated operations. See the spec for the settled decisions; red test first for every semantic unification.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] `BrowseAsync` exists and the navigate operation calls it; navigate performs no session bookkeeping of its own (no direct acquire/lock/touch/mark/note/stamp calls remain in the navigate path)
- [ ] Ordering fact: a same-URL re-browse supersedes the tab's element refs (red first — this changes the URL-compare behaviour nothing guarded)
- [ ] Ordering fact: a tab evicted between acquisition and locking answers the closed wall, not an exception
- [ ] Ordering fact: acquire exhaustion returns its retryable wall; no exception escapes the seam
- [ ] Ordering fact: the closed wall wins over the work's own answer when the page closed during the work
- [ ] The work callback receives the tab-work context (page, URL-before, stamp start) and never a raw tab; the lease commits with the work-reported ref count and the browse supersede flag
- [ ] The acquire retry bound and ref-resolution timeout constants live in the module, not the browser
- [ ] Existing unit and integration suites stay green (unmigrated operations still drive the old verbs)
