# 02 — One streaming walk on the shared matcher

**What to build:** The disk roots' glob walks the tree once, lazily, matching each entry as it is
found with the same matcher every other mount uses. A caller sees exactly what it saw before:
the same entries, sorted, capped at two hundred, with the same errors. What changes is that the
walk can now be stopped part way, which is what the next ticket needs.

Today the tree is walked twice, once for directories and once for files, and the results are
matched in a batch that only reveals what matched after consuming everything. That shape cannot
stop early. One pass whose entries carry a directory flag replaces both walks: each entry is seen
once, the deduplication step disappears because duplicates can no longer arise, and the
directories-only rule is answered from the flag rather than from which walk produced the entry.

The walk skips symlinks by explicit option, and it must keep doing so. That guard is what makes a
link pointing at its own ancestor unwalkable, and nothing fails loudly when it is dropped — the
walk keeps returning plausible results and simply never ends. It matters more once the sandbox
image gains an alias from the mount point to the container root, which is a link at the root
pointing at the root.

**Blocked by:** 01 — the characterisation suite must be green before the matcher moves.

**Status:** ready-for-agent

- [ ] The filesystem client's glob yields entries lazily rather than returning a completed
      collection, and its caller can stop consuming it.
- [ ] One recursive enumeration serves both files and directories, with each entry carrying
      whether it is a directory.
- [ ] Matching happens per entry, using the shared glob matcher, so one pattern language covers
      every mount.
- [ ] Every case from ticket 01 still passes, with no change to entries, ordering, cap or errors.
- [ ] A link pointing at its own ancestor is neither listed nor entered: the walk terminates, the
      link is absent, and the entry count stays small.
- [ ] The reparse-point skip is carried onto the new enumeration and covered by that case, so
      removing it fails a test rather than producing an endless walk.
