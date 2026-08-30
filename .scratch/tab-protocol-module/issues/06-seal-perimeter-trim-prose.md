# 06 — Contract: seal the perimeter, trim the prose

**What to build:** The tab protocol's interface becomes exactly the four per-intent methods plus pool lifecycle. The nine protocol verbs go private; the tab, routing-union, acquisition and stamp-lease types go internal; the test-only sessions accessor on the browser is deleted. The unit policy facts (LRU, reuse-before-open, ranges, tombstones, counters, idle pruning) finish porting to the four methods so nothing drives a verb directly. The web-browsing rules file's tabs section is rewritten to point at the module, keeping only what code cannot hold (settings, wall semantics for prompt authors) — the ordering prose and the lock-ordering warnings now live as code and tests, so they leave the prose. No new ADR; the domain glossary is untouched.

**Blocked by:** 05 — Integration tests infer from tool results.

**Status:** done

- [x] Public surface of the pool module = four per-intent methods + lifecycle (create/get/close/clear/prune/dispose); the nine verbs are private
- [x] Tab, routing, acquisition and lease types are internal; no caller outside the module names them
- [x] The test-only sessions accessor is gone; the whole solution compiles without it
- [x] All unit policy facts drive the public methods; none constructs the protocol sequence itself
- [x] The rules file's tabs section points at the module and duplicates no ordering it now enforces
- [x] Full `dotnet test` green (unit + integration; eval untouched per repo policy)

## Comments

Implemented 2026-08-29. Notes:

- The nine verbs: eight went `private`; `BeginStampAsync` went `internal` because the module-owned
  `TabWorkContext` is a top-level class in the same file and calls back into it — nothing outside
  the assembly can reach it, and no test does. `MarkTabNavigated` had no callers left (the
  pipeline's tail supersedes directly) and was deleted outright.
- `BrowserTab`, `RefRouting`, `TabAcquisition`, `RefStampLease`, `RefRangeState`,
  `SessionRefCounters` are internal; `BrowserSession` stays public for the lifecycle `Get`, with
  `CurrentTab`/`Tabs`/`Counters` internal.
- Both unit suites now drive only the four methods plus lifecycle: routing is observed as the
  model would see it (the wall, or which page the work ran on), lock-held races are staged by a
  work callback blocked inside the pipeline, and popups enter through the page's own Popup event
  (`FakePage.RaisePopup`) — the pool's real admission path.
- The browser's test-only `Sessions` accessor is deleted; its remaining internal escape hatches
  (evaluate, route-stub, prune) all ride the seam or the lifecycle.
- The rules file's tabs section now points at the module and its four methods; the locking
  paragraph and its "never the pool gate while holding a tab lock" warning left the prose — the
  module and `TabProtocolTests` hold them.
