# 11 — Purge at twelve months

**What to build:** A topic nothing has written to for twelve months ceases to exist, taking its
history, what it is searched by and the files sent to it. Nothing is left orphaned, including
its entry in the topic index.

Expiry drops a topic's record but leaves its index entry behind, and those entries sit below
the archive horizon where nothing reads, so nothing would ever notice them. They are removed by
one range-removal at the purge cutoff: the index is scored by last write, so everything below
that cutoff is expired by definition and no scan is needed to find it.

**Blocked by:** 03 — The topic index replaces the scan.

**Status:** ready-for-agent

- [ ] A topic and its history expire twelve months after their last write, refreshed on write.
- [ ] Index entries below the purge cutoff are removed without scanning anything.
- [ ] Attachments are swept on the purge horizon instead of their own separate clock, so a topic
      and the files sent to it are removed together.
- [ ] A conversation whose files are already gone still reads, naming what was sent.
- [ ] The purge horizon and attachment retention are settings in the retention policy block.
- [ ] The thirty-day value currently defined in four places, three of them hardcoded, is gone.
- [ ] Covered against a real store with a fake clock.
