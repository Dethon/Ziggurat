# 07 — Unread comes from a message count and a read position

**What to build:** The unread badge on a sidebar row is the difference between how many
messages a topic holds and how many its reader has seen, answered without reading a single
message. Today it is computed by the client from history it has loaded, and by scanning
backwards for a stored message id, which returns the whole list when that id is not found.

Badges look the same as they do now. This exists so that ticket 08 can stop loading history
without every badge silently reading zero.

Land the read position beside the stored message id first and remove the id once nothing reads
it, so the contract change never breaks a working client mid-flight.

**Blocked by:** 03 — The topic index replaces the scan.

**Status:** ready-for-agent

- [ ] A topic carries how many messages it holds.
- [ ] Read state is a position rather than a message id.
- [ ] Unread is the difference between the two and reads no messages.
- [ ] The stored message id is removed once nothing reads it.
- [ ] The migration marks every existing topic fully read.
- [ ] Marking a topic read on open still works.
- [ ] Advancing read state while a reply streams into the topic being viewed still works, and
      still does not advance for a topic that is not being viewed.
- [ ] Badges are unchanged to look at.
