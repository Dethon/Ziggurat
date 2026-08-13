# 06 — A search costs the same whatever a file's size

**What to build:** Searching a directory that holds an enormous file works, and costs no more
memory than searching one that holds a small one.

Each candidate file is currently read whole into memory before any line is matched. The
files-read budget from ticket 05 caps how many files that happens to, not what each one costs, so
a single large log — an allowed extension, and often exactly the file worth searching — is
materialised entirely to find one line in it.

Lines are read lazily instead, holding only what a match needs: the matching line and the context
lines around it. Peak memory becomes constant per file whatever the file's size. Skipping files
over a size threshold was considered and rejected, because the large file is usually the one the
search was for.

Nothing a caller sees changes: the same matches, the same context, the same counts and flags.

**Blocked by:** 05 — the read loop it rewrites is the one that ticket makes cancellable and
budgeted.

**Status:** ready-for-agent

- [x] Searching a file far larger than the process would comfortably hold returns its matches with
      the requested context.
- [x] Peak memory while searching does not scale with the size of the largest file examined.
- [x] Matches, context lines before and after, line numbers and the output modes are identical to
      today's for every existing case.
- [x] Context near the first and last lines of a file behaves as it does today.
- [x] An unreadable file is still skipped rather than failing the search.
- [x] The budgets, flag and counts from ticket 05 are unchanged.
