# 06 — Settings, locks, and the idle clock

Status: ready-for-agent
Blocked by: 02

**What to build:** The operational edges. The tab cap and the session idle timeout (today
hard-coded at the browser's construction site) become settings in the browse server's own
`appsettings.json` — generic tunables, no compose entry, per the configuration rules. Every routed
call refreshes the session's idle clock, so an actively acting session can no longer be pruned
mid-use. A per-tab lock serializes navigation, action, snapshot and image fetch on one tab —
snapshot and fetch take no lock today — and tab-pool mutation (create, reuse, evict, adopt)
happens under a session-level gate. The web-browsing rules file gains its tabs section here, when
the constraints describe shipped code.

- [ ] Tab cap and idle timeout read from the server's settings, defaulting to three and thirty minutes
- [ ] A session doing only actions, snapshots and image fetches for longer than the idle timeout is not pruned
- [ ] A snapshot or image fetch racing a navigation on the same tab waits rather than reading a half-replaced DOM
- [ ] Two calls on different tabs of one session do not serialize against each other
- [ ] The rules file's web-browsing section documents tabs, routing, the two walls and the touch rule
- [ ] Covered at the session-management unit seam (clock, lock ordering observable as outcomes, settings binding), never by asserting lock state directly
