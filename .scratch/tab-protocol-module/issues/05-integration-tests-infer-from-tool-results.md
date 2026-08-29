# 05 — Integration tests infer from tool results

**What to build:** The integration suite observes the tab pool only through the browser contract — walls and results — never by reaching past the seam. Rewrite every assertion that currently inspects the pool directly (the test-only sessions accessor and the evaluate/annotate/initialize helper reaches, spread across the integration files) into behaviour the model itself could see: a superseded ref answers the superseded wall; a re-browse of a held URL reports reuse; the browse past the tab cap evicts the least-recently-touched tab, observable because that tab's refs answer the closed wall; an idle-pruned session answers no-session. Any pool state genuinely unobservable through the contract is a finding — record it in this ticket's Comments as an interface gap, don't reintroduce a backdoor.

**Blocked by:** 02 — Current-tab and by-URL intents · 03 — By-ref intent · 04 — Image fetch per-ref.

**Status:** ready-for-agent

- [ ] No integration test reaches past the browser contract into the pool (no sessions accessor, no session-scoped evaluate/annotate helpers used as assertions)
- [ ] Tab-pool behaviours (reuse, LRU eviction at the cap, supersede on re-read, idle expiry) are each asserted via walls and results
- [ ] Any state found unobservable through the contract is written up under Comments as a finding, not worked around
- [ ] Integration suite green (fixtures may keep a legitimate warm-up hook; warm-up is not an assertion)
