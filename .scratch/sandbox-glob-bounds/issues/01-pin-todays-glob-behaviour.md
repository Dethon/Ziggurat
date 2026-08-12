# 01 — Pin today's glob behaviour

**What to build:** The glob semantics that ship today, written down as tests, so that changing the
matcher underneath cannot alter what patterns match without a named failure. Nothing a caller can
observe changes in this ticket: it is the green suite the next one has to keep green.

The disk roots currently match with a batch matcher; every other mount matches with the shared
glob matcher, whose own comment claims to mirror the disk one. "Claims to mirror" is not "proven
to mirror", and the proof has to exist before the swap, not after.

Cover the edges most at risk in that swap, against a real temporary tree: dotfiles and whether
they match a leading wildcard, patterns anchored at the base level, a recursive segment matching
zero segments, a recursive segment matching many, brace alternation, single-character wildcards,
the trailing slash asking for directories only, and a pattern that matches nothing.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The local filesystem client's existing glob scenario table covers each of the listed edges,
      asserting the entries returned rather than how they were found.
- [ ] Every new case passes against the code as it stands, with no production change in this
      ticket.
- [ ] Where a case documents behaviour that looks accidental rather than intended, it is written
      as the behaviour that ships and noted as such, so the swap is judged against reality.
