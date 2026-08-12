# 03 — The topic index replaces the scan

**What to build:** The topics for an agent and a space are read from the topic index, ordered
most recently written first. Finding them by scanning every key in the store is gone. The
sidebar looks exactly as it did.

See ADR 0024 for why the index is the only read path.

**Blocked by:** 02 — A topic's last-write time is stamped server-side.

**Status:** ready-for-agent

- [x] Saving a topic places it in the index at its last-write time; deleting a topic removes it.
- [x] The list is read only from the index.
- [x] A one-time migration, run from the Agent host on start-up, builds the index for topics
      that already exist.
- [x] The scan-based listing is deleted rather than kept as a fallback, and nothing can reach it.
- [x] A channel started against a store the migration has not reached serves an empty list
      rather than falling back to a scan.
- [x] The sidebar is unchanged to look at.
