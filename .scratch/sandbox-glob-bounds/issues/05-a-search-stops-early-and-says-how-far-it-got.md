# 05 — A search stops early and says how far it got

**What to build:** A text search on a disk root costs bounded work in the two ways it actually
costs, can be stopped, and reports how much of the tree it covered.

A search stops once it has enough matches, which is why a search that finds things is cheap. A
search that finds nothing walks every directory under the root and reads every file with an
allowed extension, and on the sandbox mount the root is the whole container. The two costs are
different in kind: enumerating a name is a stat, examining a file is a read. So there are two
budgets — the same fifty thousand entries the glob walk uses, covering enumeration, and five
thousand files opened and read, covering examination. A file pattern that excludes everything is
bounded by the first; a pattern that excludes nothing is bounded by the second.

The search takes a cancellation token, which it does not currently accept at all, and honours it
per file, so a caller who disconnects stops the walk instead of leaving it running.

The response carries the same coverage flag as the glob result, spelled identically so the word
means one thing, and a count of entries examined beside the existing count of files searched. A
search whose file pattern excluded everything then reads as a large scan against zero files
searched, which is the case no flag alone can explain.

Only disk roots are bounded. The backends whose entries are a finite in-memory set — Home
Assistant, schedules, timers, the print queue — have no tree to walk away into and are untouched.

The sandbox prompt changes here, once both operations are bounded: it stops saying these work as
on any other mount and says the mount root is the whole container, so a glob or a search there is
scoped deliberately. It is advice, not a rule the code enforces — a root-level search is
legitimate when it is what was meant.

**Blocked by:** 03 — the shared budget constant and the coverage flag are introduced there.

**Status:** ready-for-agent

- [x] A search whose file pattern matches nothing stops at the entries-scanned budget and says so,
      rather than walking the whole tree.
- [x] A search that matches no content stops at the files-read budget and says so.
- [x] The entries-scanned budget is the same definition the glob walk reads; the files-read budget
      sits beside it, and both are reachable from a test without building a tree that size.
- [x] The search result reports the coverage flag and a count of entries examined, beside the
      existing count of files searched.
- [x] Truncation on reaching the requested match count keeps its current meaning and stays
      distinct from the coverage flag.
- [x] Cancelling a search part way ends the walk and propagates the cancellation.
- [x] A link pointing at its own ancestor is neither searched nor entered, and the walk terminates.
- [x] Searching a single named file is unaffected — no walk, no budgets.
- [x] The backends with finite in-memory entries are unchanged and always report the flag as false.
- [x] The search description states both budgets and the coverage flag.
- [x] The sandbox prompt no longer implies a recursive glob or search at the mount root is an
      ordinary thing to do, and names the root as the whole container.
