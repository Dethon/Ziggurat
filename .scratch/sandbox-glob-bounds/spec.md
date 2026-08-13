# A glob or a search on a disk root does bounded work

Status: done

## Problem Statement

Globbing the sandbox mount walks the entire container. The mount is the container root, so an
unqualified recursive pattern enumerates every path in the image, and the sandbox prompt actively
invites it by saying glob works there as on any other mount. Nothing bounds the walk: no cap on
entries enumerated, no timeout, and the two-hundred-entry response cap is applied only after the
whole enumeration has finished. Peak memory scales with the container's total path count rather
than with the two hundred entries that come back.

Searching the same mount has the same shape. The scan stops once it has enough matches, but a
search that matches nothing reads every file with an allowed extension under the root, and each
one is read whole into memory, so a single large log costs its own size regardless of how many
files the search touches.

Neither operation can be stopped. Glob accepts a cancellation token and never uses it; its body is
synchronous, so a caller who disconnects or cancels leaves the walk running to completion holding
the request thread. Search does not accept a token at all.

The agent has no way to see any of this. It cannot tell an empty result that means "nothing
matched" from one that means "I walked as far as I was willing to and stopped", and it cannot tell
how much of the tree either answer covers. So it retries, refines a pattern that was never the
problem, and pays the full walk again.

## Solution

Both walks become bounded, stoppable and honest.

A glob or a search does an amount of work fixed by two budgets that hold whatever the caller asks
for: how many entries may be enumerated, and how many files may be opened and read. Reaching
either one ends the walk and is reported in the response, so the agent can tell reduced coverage
from a genuine absence of matches, and can see how much was actually covered rather than inferring
it.

The response cap stops being something applied after the fact. A glob stops walking as soon as it
has more matches than it will return, so the common case of a broad pattern over a large tree
returns almost immediately instead of enumerating everything and then discarding it.

Cancelling stops the work. Both operations honour the caller's token, and a caller who disconnects
stops paying for a walk nobody is waiting on.

Reading a file no longer costs that file's size. A search holds only the lines it needs to report
a match with its context, so file size stops being a memory risk.

The sandbox prompt stops presenting a root-level glob or search as an ordinary thing to do, and
both tool descriptions state the budgets and what the response says when one is reached.

## User Stories

1. As the agent, I want a glob to cost work proportional to what I asked for, so that a broad
   pattern does not enumerate an entire container before answering.
2. As the agent, I want a glob over a large tree to return promptly once it has enough entries to
   fill the response, so that I am not charged for results that are discarded.
3. As the agent, I want to be told when a walk stopped before the tree ended, so that I can tell
   incomplete coverage from an empty match set.
4. As the agent, I want that signal to be distinct from the existing truncation signal, so that
   "more matched than fit" and "I stopped looking" are never confused for one another.
5. As the agent, I want to see how many entries a walk examined, so that a bounded answer tells me
   how much of the tree it covers rather than only that it was bounded.
6. As the agent, I want a search that excluded everything by file pattern to report a large scan
   against zero files searched, so that I can tell a bad pattern from an empty directory.
7. As the agent, I want a search to be bounded in the two ways it actually costs, so that neither
   a pattern matching no files nor a pattern matching every file can run unbounded.
8. As the agent, I want a search over a directory holding one enormous file to succeed, so that
   file size is not a reason for the operation to fail or stall.
9. As the agent, I want both budgets stated in the tool descriptions, so that I can anticipate the
   bound instead of discovering it by paying for it.
10. As the agent, I want the sandbox prompt to tell me the mount root is the whole container, so
    that I scope a glob or a search deliberately rather than being invited to walk everything.
11. As the agent, I want the entries in a glob response to remain full virtual paths I can pass
    straight into another filesystem tool, so that a bounded answer is no less usable than an
    unbounded one was.
12. As the agent, I want glob results to remain sorted within the response, so that reading a
    listing is no harder than it was.
13. As the agent, I want a pathological pattern to come back as the same timeout envelope on a disk
    root as on every other mount, so that one failure has one shape wherever I meet it.
14. As the agent, I want a base path that does not exist to keep answering the not-found envelope,
    so that a mistyped path reads as a mistake rather than as an empty result.
15. As the agent, I want the glob semantics on a disk root to match the ones on every other mount,
    so that one pattern language means one thing everywhere.
16. As the agent, I want a live download's status file to appear in a media library listing even
    when the directory holds more ordinary files than the response can carry, so that I can see
    what is downloading.
17. As the person using the assistant, I want a request that involves searching the sandbox to
    finish in a reasonable time, so that a broad question does not stall the conversation.
18. As the person using the assistant, I want cancelling or navigating away to actually stop the
    work, so that an abandoned request stops consuming the machine.
19. As the person using the assistant, I want the agent to stop retrying a search that was bounded
    rather than wrong, so that turns are not spent re-running the same expensive walk.
20. As the person using the assistant, I want the assistant to remain responsive while one
    conversation is searching a large tree, so that one expensive call does not degrade the rest.
21. As an operator, I want a filesystem call to have a ceiling on the work it can start, so that a
    single agent turn cannot saturate a container's I/O.
22. As an operator, I want memory during a glob or a search to stay flat regardless of tree size
    or file size, so that the sandbox container's usage is predictable without a hard limit on it.
23. As an operator, I want a symlink loop inside a mounted tree to be impossible to walk into, so
    that a link pointing at its own ancestor cannot hang a request.
24. As a developer, I want the bounds expressed as one definition each, so that the glob walk and
    the search walk cannot disagree about the same number.
25. As a developer, I want the budgets reachable from a test without building an enormous tree, so
    that the bound is asserted directly rather than trusted.
26. As a developer, I want the walk to skip symlinks by an explicit option that a test protects,
    so that removing it fails a build rather than producing an endless walk in production.
27. As a developer, I want one glob matcher in the repo, so that a change to pattern semantics
    cannot land on some mounts and not others.
28. As a developer, I want the matcher swap proved against the behaviour that shipped, so that
    unifying the semantics does not silently change what patterns match.
29. As a developer, I want the timeout envelope shared rather than reimplemented per call site, so
    that a mount cannot answer a pathological pattern differently from its neighbours.
30. As a developer, I want backends whose entries are finite in memory to be untouched, so that a
    bound only exists where a walk can actually run away.
31. As a developer adding a new backend, I want the coverage flag to default to false, so that a
    backend which cannot run out of tree does not have to say so.
32. As a developer, I want a cancelled call to propagate as the abort it is, so that this operation
    does not become the one exception to how cancellation is handled.

## Implementation Decisions

**The bound belongs to the shared disk code, not to the sandbox.** The unbounded walk is in the
local filesystem client and the disk text search tool, both shared by every disk root. The sandbox
is where it bites, because its root is the container root, not where the defect lives. Fixing only
that mount would mean a second glob implementation, so the brace expansion, the dirs-only
trailing-slash rule and the symlink skipping would exist twice and drift.

**The walk stays in process.** Delegating the sandbox's glob to a shell command inside the
container was rejected: it makes that mount's glob semantics an artefact of another tool's flags
rather than of the shared matcher, and it fixes only the mount that happens to support exec while
leaving the same walk under the vault and the media library.

**Two budgets, each measuring a cost that actually arises.** Entries enumerated is capped at fifty
thousand and covers both walks, so a pattern that excludes every candidate still cannot walk a
container. Files opened and read is capped at five thousand and applies to search alone, because
reading bytes is a different order of expense from enumerating a name. Both live as one definition
each in the domain layer, read by both walks, so the two cannot drift apart.

**The budgets are constants, not settings.** Nothing deployment-specific decides them, so they sit
beside the existing two-hundred-entry response cap rather than in configuration. Each is
overridable for tests, following the existing precedent of the search match timeout, which is
overridable for exactly the same reason.

**Glob becomes a stream, and the cap stops the walk.** The filesystem client's glob returns an
asynchronous sequence and enforces the scan budget itself, because it is the only component that
can count entries. The glob tool stops pulling once it has one more match than it will return, so
the response cap now ends the walk rather than trimming its output. The cap stays a
response-shaping concern in the tool; the budget stays a work concern in the client.

**One walk instead of two.** The two recursive enumerations, one for directories and one for
files, collapse into a single pass whose entries carry a directory flag. Each entry is counted
against the budget exactly once, the deduplication step disappears because duplicates can no
longer arise, and the dirs-only rule is answered from the flag.

**The disk roots adopt the shared glob matcher.** Per-entry matching is what makes early exit
possible, and the batch matcher used today cannot answer per entry: it reveals matches only after
consuming its whole input. The disk roots move onto the same matcher the Home Assistant and
schedule backends already use, so one pattern language covers every mount.

**Selection becomes the first entries found rather than the lexically first.** Today the client
sorts every match and the tool takes the first two hundred, which requires the complete match set.
Stopping early means the two hundred returned are the first two hundred encountered, sorted for
presentation. The trade is deliberate: which two hundred come back now depends on directory
enumeration order and is not reproducible across runs, and that is the price of an early exit that
makes the common broad pattern cheap. The alternative, keeping a bounded set of the lexically
smallest matches while walking, preserves determinism but forfeits the early exit entirely,
because it must always scan to the budget.

**The response says what was covered.** Both the glob result and the search result gain a flag
recording that a budget ended the walk, spelled identically on both so the word means one thing,
and distinct from the existing truncation flag, which continues to mean that more matched than
fit. Both gain a count of entries examined; the search result already reports files searched, so
between them the two budgets each have a visible number. The glob result's total now means matches
found before stopping rather than the true match count. The new flag is a defaulted member rather
than a required one: backends whose entries are a finite in-memory set cannot trip it, and a
response from an older server still deserializes.

**Cancellation propagates.** Both walks check the caller's token, and cancellation leaves as the
abort it is rather than becoming a successful-looking partial answer, which is the rule the shared
call-tool error filter already applies. The search tool gains a cancellation token, which it does
not currently take at all.

**The timeout envelope is shared rather than duplicated.** Using the shared matcher on a disk root
introduces a match timeout the disk path has never been able to hit. The existing base-class glob
helper both catches that timeout and builds a fixed result that cannot express truncation, so the
catch is extracted into a wrapper the disk path can use while building its own result. The
existing helper keeps its behaviour by calling the wrapper, and no other backend changes.

**The not-found envelope moves with the enumeration.** A lazy sequence raises a missing directory
on the first pull rather than at the call, so the catch that turns it into the not-found envelope
wraps the pull loop. Probing for existence first was rejected: it costs an extra stat on every
glob and the directory can still vanish afterwards, so the catch is needed regardless.

**A search reads lazily.** Files are read line by line with a small buffer holding only the
context lines around a match, so per-file memory is constant whatever the file's size, and no file
is excluded for being large. Skipping oversized files was rejected because the large log is often
exactly the file worth searching.

**The search bound applies to disk roots only.** The base-class scan that serves the virtual
backends iterates a finite in-memory node set with no tree to walk away into, so a budget there
would be dead code carrying a flag that can never be true.

**The media library merge reserves the overlay's entries.** The mount merges its disk walk with
the rendered entries a live download owns, then applies the response cap. Today the disk entries
come first, so a directory holding more than a capful of ordinary files pushes a live download's
status file out of its own listing. The merge is changed to reserve the overlay entries, there
being at most as many as there are live downloads, then fill the remainder from the disk walk and
sort for presentation. This upholds the rule the mount already follows everywhere else: a live
download owns its path. The coverage flag and the scanned count pass through from the disk half,
since the overlay enumerates nothing.

**The symlink guard is carried onto the new walk, deliberately.** The enumeration skips reparse
points by explicit option, which is what makes a symlink loop unwalkable. This matters more once
the sibling change adds an alias from the mount point to the container root, since that alias is a
link at the root pointing at the root. The guard, not the shape of the tree, is what keeps the
walk finite, and the scan budget stands behind it as an independent backstop.

**Documentation carries the contract.** Both tool descriptions state the budgets and the coverage
flag, and the glob descriptions drop the claim that results are lexically selected. The sandbox
prompt stops saying these operations work as on any other mount and states that the mount root is
the whole container, covering globbing and searching together, as advice rather than as a rule the
code does not enforce. No architecture decision record and no glossary term: the decisions live
here.

**This work is sequenced after the sandbox path-consistency change.** That change introduces the
alias at the container root, and the characterisation and termination tests here are worth more
when they run against the tree the walk will actually meet.

## Testing Decisions

A good test here asserts what a caller observes: which entries come back, what the flags and
counts say, whether the walk terminates, and whether cancelling stops it. None of them should
reach into how the walk is implemented, because most of this change is a rearrangement behind
answers that are meant to stay the same. The two places where an answer genuinely changes, the
selection rule and the merge order, are the two that deserve the most direct assertions.

Four seams, all of them existing. No new test layer is introduced.

**The local filesystem client's integration tests are the seam for the glob walk.** They already
drive the real client against a real temporary tree, with a table of glob scenarios and a symlink
case. They gain characterisation cases first, then bound cases, then a termination case.

**The disk text search tool's unit tests are the seam for the search walk.** They already exercise
real files, so the read budget, the lazy read, the token and the coverage flag are all assertable
there without a new fixture.

**The media library's filesystem unit tests are the seam for the merge.** They already drive the
mount against a fake client and a fake download manager, so a case with more entries than the
response cap plus one live download asserts that the status file survives. That case fails today.

**The glob tool's unit tests are the seam for the cap and the result fields.** They already drive
the tool against a fake client, which is the level at which the response cap and the reported
totals are decided.

Nothing is added at the virtual filesystem tool boundary. The new fields are data carried through
unchanged, and the existing conformance theories already drive every tool derived from the one
operation list; asserting two booleans and two counts there would test serialization rather than
behaviour.

**The order of work is characterise, swap, then bound.** First pin the behaviour that ships today
as scenario cases against the client, including the edges most at risk in a matcher change:
dotfiles, patterns anchored at the base level, recursive segments matching zero segments, and the
dirs-only trailing slash. Then move to the shared matcher under a green suite, so any divergence
appears as a named failure rather than as a behaviour change nobody noticed. Only then add the
streaming, the budgets, the flags, the counts and the cancellation. The risky step is isolated in
the middle with tests either side of it.

**The budgets are reached by lowering them, not by building a tree that trips the shipped
number.** Prior art is direct: the Home Assistant filesystem's timeout tests inject a tiny match
timeout to trip a bound without waiting for a real second, and the base backend's tests trip the
glob timeout the same way.

**Termination is asserted for both walks.** A case builds a link pointing at its own ancestor and
asserts that the walk finishes, that the link is absent from the results and that the entry count
stays small. The existing symlink test covers a link pointing outside the tree, which is a
containment property; its comment mentions cycling, but no case has ever built a cycle. Both the
glob walk and the search walk run against the same tree, since both carry the same guard and both
are changed here.

**Cancellation is asserted where the walk is, not at the tool boundary.** A token cancelled part
way through a walk should end it as an abort, and the assertion belongs beside the walk it stops.

## Out of Scope

**A memory limit on the sandbox container.** Once neither walk holds a tree or a whole file in
memory, the glob no longer motivates one. Limiting a container whose purpose is running arbitrary
user code is an operational decision with its own failure mode, and it is deliberately not filed.

**Whole-file reads elsewhere.** The text read tool also reads a file whole, but reading a named
file is work the caller asked for and it already takes an offset and a limit. Unchanged.

**A recursion depth ceiling.** Rejected twice over: as a primary bound it is pattern-dependent and
refuses legitimate deep trees, and as a loop guard it is redundant beside the symlink skip and the
scan budget, while adding a limit the caller cannot see.

**An end-to-end test through the compose stack.** The bound is proved deterministically at the
integration seam with a lowered budget. Nothing about the container image is under test here,
unlike the alias in the sibling change, and a timing assertion against a real container would be
host-dependent.

**Bounds on the virtual backends.** Their nodes are a finite in-memory set, bounded by
construction.

**Making glob selection deterministic again.** Explicitly traded away for the early exit. If it
ever matters, the bounded-lexical-set approach is the alternative that was considered and
rejected.

**The mount alias appearing in its own listing.** Because the alias is a reparse point like any
other, it is skipped as an entry, so a listing of the container root shows every top-level
directory except the alias itself. Nothing reads it: the mount point is addressed as a mount
point, and every path beneath it resolves normally.

## Further Notes

The symlink loop question was settled by measurement rather than argument. A probe built a tree
containing a link at its root pointing at that same root, the shape the alias will have, and
enumerated it both ways. With reparse points skipped, both the current enumeration API and the
custom enumerable this change introduces returned four entries in about a millisecond and never
entered the link. With the skip removed, the same tree yielded five thousand entries at a nesting
depth of twenty in fifty milliseconds and was still going when the probe cut it off. The guard is
load-bearing, and it holds for the self-referential case specifically.

That is also why the guard deserves its own assertion. Nothing fails loudly when it is dropped:
the walk keeps returning plausible-looking results, it simply never ends.

The finding that started this was made while checking whether a new alias in the sandbox image
would make globbing recurse. It would not, and the unbounded walk predates that change entirely.
