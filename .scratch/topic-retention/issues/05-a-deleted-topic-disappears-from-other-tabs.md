# 05 — A deleted topic disappears from other tabs

**What to build:** Deleting a topic in one client removes its row from every other client in
the space, without a refresh. Today the delete succeeds but says nothing, so a second tab keeps
showing a row that no longer exists. The notification type and the client's handler for it
already exist; only the send is missing.

Pagination makes this worse, which is why it is fixed here rather than left.

**Blocked by:** 01 — One topic store behind the interface.

**Status:** ready-for-agent

- [x] Deleting a topic broadcasts the deletion to its space.
- [x] A second client removes the row without being refreshed.
- [x] Deleting a topic that is already gone is harmless and still broadcasts nothing wrong.
