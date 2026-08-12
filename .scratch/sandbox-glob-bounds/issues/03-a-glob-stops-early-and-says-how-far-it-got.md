# 03 — A glob stops early and says how far it got

**What to build:** A glob costs work proportional to what was asked for, and its answer says how
much of the tree it covers.

Two things end the walk. The response cap ends it: once the walk has more matches than the
response will carry, there is nothing left to find that anyone will see, so it stops. A scan
budget ends it: after fifty thousand entries have been enumerated, the walk stops wherever it is,
however few matches it has. Cancelling ends it too, and a cancelled call propagates as the abort
it is rather than answering as though it succeeded.

The response reports which happened. Truncation keeps its meaning — more matched than fit — and a
new flag says the walk stopped before the tree ended, so an empty answer that means "nothing
matched" is no longer confusable with one that means "I stopped looking". A count of entries
examined comes back beside it, so the bound is a number the caller can read rather than a claim
in a description. The total now means matches found before stopping.

Selection changes as a consequence, and this is the deliberate trade: the entries returned are the
first ones found, sorted for presentation, rather than the lexically first of the complete match
set. Which ones come back depends on enumeration order and is not reproducible across runs. That
is the price of an early exit, and it is what makes a broad pattern over a large tree cheap.

Two error paths move with the walk. Using the shared matcher gives the disk roots a match timeout
they have never been able to hit, so the timeout envelope every other mount answers has to reach
this path — the existing base helper both catches that timeout and builds a result shape that
cannot express truncation, so the catch belongs in a wrapper the disk path can use while building
its own result. And a lazy sequence raises a missing directory on the first pull rather than at
the call, so the catch that turns it into the not-found envelope has to wrap the pull.

**Blocked by:** 02 — the walk has to be lazy before anything can stop it.

**Status:** ready-for-agent

- [ ] A glob over a tree with far more matches than the response carries returns promptly, having
      enumerated near the cap rather than the whole tree.
- [ ] A glob over a tree with no matches stops at the scan budget and says so.
- [ ] The budget is one definition in the domain layer, reachable from a test without building a
      tree that size, following the precedent of the search match timeout.
- [ ] The glob result reports the coverage flag and a count of entries examined; the total means
      matches found before stopping.
- [ ] Truncation and the coverage flag are independently observable: a capped-but-complete walk
      sets one, an exhausted budget sets the other.
- [ ] Cancelling a glob part way ends the walk and propagates the cancellation.
- [ ] A pattern that trips the matcher's timeout answers the same timeout envelope a non-disk
      mount answers.
- [ ] A base path that does not exist still answers the not-found envelope.
- [ ] Entries remain full virtual paths, sorted within the response, usable directly as input to
      another filesystem tool.
- [ ] The glob descriptions state the budget and the coverage flag, and no longer claim results
      are selected lexically.
