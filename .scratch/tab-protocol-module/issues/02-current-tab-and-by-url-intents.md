# 02 — Current-tab and by-URL intents: snapshot migrates

**What to build:** Snapshots run through the new seam. Two more per-intent methods: `OnCurrentTabAsync(work)` for the ref-less default (snapshot of the current tab, get-current-page) and `OnUrlAsync(url, work)` for the composed-snapshot exception — addressed to the tab holding that URL, answering the tab-gone wall if the browse session no longer holds it (per ADR-0034's special case). The pipeline from ticket 01 is reused; these methods only differ in how the tab is found and in the supersede rule (non-browse: supersede iff the URL moved across the work). The re-navigated-while-queued check (requested vs final URL both compared) moves inside the module. The snapshot and get-current-page operations migrate; their session bookkeeping leaves the Playwright browser.

**Blocked by:** 01 — Pipeline + browse intent.

**Status:** ready-for-agent

- [ ] `OnCurrentTabAsync` and `OnUrlAsync` exist; snapshot (both addressing modes) and get-current-page call them and perform no session bookkeeping of their own
- [ ] A snapshot addressed to a URL whose tab is gone answers the named wall — no composed answer is ever built from a different page
- [ ] Ordering fact: a snapshot queued behind a navigation of the same tab detects the re-navigation under the lock and answers accordingly
- [ ] Ordering fact: a snapshot (non-browse intent) whose work does not move the URL leaves earlier ref ranges routing
- [ ] Snapshot's element-ref stamping (restamp vs augment) flows through the method's stamping policy; no lease crosses the seam
- [ ] Existing suites stay green; action, back and image fetch still drive the old verbs
