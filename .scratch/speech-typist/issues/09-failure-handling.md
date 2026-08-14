# 09 — Failure handling

**What to build:** a network blip in the middle of a sentence costs you that phrase and nothing more,
and a Lemonade that is down tells you once so you stop talking into nothing.

A failed request — timeout, connection refused, a 500 — is retried once. If the retry fails too, that
segment is dropped and the segments after it proceed normally, because the alternative is one blip
costing the whole dictation. The prompt chain skips the segment that never produced a transcript
rather than stalling on it.

The tray moves to its error state, and one notification is raised per dictation rather than one per
segment: with Lemonade down every segment fails, and a notification each would be a stream of them
while you are still speaking. The error state clears on the next transcript that arrives, so it never
needs a manual reset.

**Blocked by:** 05.

**Status:** resolved

- [x] A failed request is retried exactly once
- [x] After the retry fails the segment is dropped and the following segments still transcribe and
      inject
- [x] With Lemonade unreachable for a whole dictation, exactly one notification is raised
- [x] The tray shows its error state, distinct from idle, recording and transcribing
- [x] The error state clears on the next successful transcript with no manual action
- [x] The prompt chain is unaffected by the segment that produced nothing
- [x] Retry, drop, notification count and tray transitions are all covered through the fake host

## Comments

2026-08-14 — Done. The behaviour was already in the core from ticket 02; this ticket is the
tests that pin it. They were checked against a mutation — letting the retry run three times
instead of two fails five of them — so they are not passing by accident.
