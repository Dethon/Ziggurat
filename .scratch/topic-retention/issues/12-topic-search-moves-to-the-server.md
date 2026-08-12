# 12 — Topic search moves to the server

**What to build:** Searching the sidebar finds topics the client never loaded. Today the search
box filters rows already in the client, which stops working entirely once the list is paged.

**Blocked by:** 04 — The topic list pages.

**Status:** ready-for-agent

- [x] Searching by name is a hub call and matches topics that were never loaded.
- [x] Results span both the ordinary and the archived ranges.
- [x] The client-side filter over loaded rows is removed.
- [x] Results are paged like any other list of topics.
- [x] Searching while not live behaves the way every other hub call does.
