# The one-word acknowledgement, checkable after all

Status: resolved

## Answer

3/3 armed on 2026-08-19: every run opened with exactly one word before the search.

`voice.one-word-before-slow-work` sat as needs-fixture with the reason "the recording holds
one reply; the acknowledgement is a separate emission". The premise is stale:
`ReplyChecks.WithoutAcknowledgement` exists because recorded replies do open with the one-word
sentence when the model emits it — the emission is separate on the channel, but the text
reaches the recording.

Split the claim along what a recording can see:

- **Shape** — the reply's first sentence is exactly one word: `OpensWithAcknowledgement` on
  `ReplyExpectation`, a new deterministic check, three unit tests. Carried by "slow spoken
  work opens with one word": a spoken search turn (search + browse is slow work by the
  contract's own words), the museum's opening time as the answer.
- **Timing** — that the word is spoken before the tools run is the channel's streaming
  behaviour; a recording of the finished reply cannot witness it, and no exemption is needed
  for it because the claim's falsifiable half now runs.
