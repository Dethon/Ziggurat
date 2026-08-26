# 02 — A browse session holds tabs

Status: resolved
Blocked by: 01

**What to build:** A browse session keeps up to three pages alive at once. `web_browse` reuses a
tab whose URL matches the request — by the address asked for or the address landed on, exactly —
reloading it in place and restamping its refs with fresh numbers; any other URL opens a new tab.
At the cap, the least-recently-touched tab is closed. Touch is any tool call routed to a tab,
`view_image` included, and the tab last touched is the session's current tab. Tabs are invisible:
no envelope, prompt or tool description gains a tab handle or a tab list.

- [x] Browsing a second, different URL leaves the first page alive — its content still answerable afterwards
- [x] Browsing a URL matching a live tab's requested or final URL reloads that tab rather than opening another
- [x] Reuse matches across a redirect: re-browsing the address originally asked for finds the tab that landed elsewhere
- [x] A fourth distinct page closes the least-recently-touched tab, not the oldest-created
- [x] Any routed call counts as touch and updates which tab is current
- [x] Two parallel browses of different URLs land on two tabs, neither result corrupted by the other
- [x] No tool result or envelope names a tab
- [x] Covered at the session-management unit seam (reuse, LRU, touch, current) with the browser faked, plus integration coverage that two real tabs hold two live DOMs
