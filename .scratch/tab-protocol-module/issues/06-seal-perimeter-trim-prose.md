# 06 — Contract: seal the perimeter, trim the prose

**What to build:** The tab protocol's interface becomes exactly the four per-intent methods plus pool lifecycle. The nine protocol verbs go private; the tab, routing-union, acquisition and stamp-lease types go internal; the test-only sessions accessor on the browser is deleted. The unit policy facts (LRU, reuse-before-open, ranges, tombstones, counters, idle pruning) finish porting to the four methods so nothing drives a verb directly. The web-browsing rules file's tabs section is rewritten to point at the module, keeping only what code cannot hold (settings, wall semantics for prompt authors) — the ordering prose and the lock-ordering warnings now live as code and tests, so they leave the prose. No new ADR; the domain glossary is untouched.

**Blocked by:** 05 — Integration tests infer from tool results.

**Status:** ready-for-agent

- [ ] Public surface of the pool module = four per-intent methods + lifecycle (create/get/close/clear/prune/dispose); the nine verbs are private
- [ ] Tab, routing, acquisition and lease types are internal; no caller outside the module names them
- [ ] The test-only sessions accessor is gone; the whole solution compiles without it
- [ ] All unit policy facts drive the public methods; none constructs the protocol sequence itself
- [ ] The rules file's tabs section points at the module and duplicates no ordering it now enforces
- [ ] Full `dotnet test` green (unit + integration; eval untouched per repo policy)
