# 05 — Integration tests infer from tool results

**What to build:** The integration suite observes the tab pool only through the browser contract — walls and results — never by reaching past the seam. Rewrite every assertion that currently inspects the pool directly (the test-only sessions accessor and the evaluate/annotate/initialize helper reaches, spread across the integration files) into behaviour the model itself could see: a superseded ref answers the superseded wall; a re-browse of a held URL reports reuse; the browse past the tab cap evicts the least-recently-touched tab, observable because that tab's refs answer the closed wall; an idle-pruned session answers no-session. Any pool state genuinely unobservable through the contract is a finding — record it in this ticket's Comments as an interface gap, don't reintroduce a backdoor.

**Blocked by:** 02 — Current-tab and by-URL intents · 03 — By-ref intent · 04 — Image fetch per-ref.

**Status:** done

- [x] No integration test reaches past the browser contract into the pool (no sessions accessor, no session-scoped evaluate/annotate helpers used as assertions)
- [x] Tab-pool behaviours (reuse, LRU eviction at the cap, supersede on re-read, idle expiry) are each asserted via walls and results
- [x] Any state found unobservable through the contract is written up under Comments as a finding, not worked around
- [x] Integration suite green (fixtures may keep a legitimate warm-up hook; warm-up is not an assertion)

## Comments

Implemented 2026-08-29. What moved and what stayed:

- Every `Sessions` accessor read is gone from the integration suite. Tab-pool state is now
  inferred through walls and results: two live DOMs via by-URL snapshots; reuse via the old ref
  answering the superseded wall after a re-browse; LRU eviction past the cap via the oldest tab's
  ref answering the closed wall; popup adoption via the ref-less snapshot landing on the popup and
  the opener still answering by URL; the click's landing tab via the action's own diff; idle
  expiry via a private browser with a millisecond idle window answering session-not-found after a
  prune.
- The evaluate/annotate helpers remain strictly as arrangement (injecting markup, planting
  images, forcing reflow, learning a planted ref) — never as assertions. They now ride the
  current-tab intent internally, so even the escape hatch performs no session bookkeeping.
- The two `page.RouteAsync` arrangements that reached through the pool for the page handle go
  through a new internal `RouteOnSessionAsync` arrange hook instead.
- `SessionUniqueRefTests`' second-pass numbering claim is observed through the contract now:
  i-1 answers superseded (renumbered, never reissued), i-2 answers the picture.
- Findings (interface gaps): none — every pool behaviour the suite asserted proved observable
  through walls and results. The one measurement suite that still reads the DOM
  (`PageImageMeasurementTests.MeasureAsync`) observes the annotator's own page-side output, not
  pool state, and stays on the page-level escape hatch.
