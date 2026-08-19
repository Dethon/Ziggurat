# Voice: the likeliest reading, spelled-out units, and the one-word opener

Status: resolved

## Answer

Armed on 2026-08-19: the likeliest light 3/3, the spelled-out temperature 3/3, and the
one-word opener 3/3 — the model says "Buscando." before the search and the word survives
into the recorded reply, which is what made the old needs-fixture reason stale.

- `voice.unclear-request-is-acted-on` — "apaga la luz" with no light named: the likeliest
  reading is the speaking room's, and asking "¿cuál?" is the failure. The ask-before-destroy
  half of the same sentence is witnessed by the vault's delete scenario.
- `voice.abbreviations-are-spelled-out` — the AC's target temperature, spoken: "grados" is
  required as a word and "°" is forbidden. The state read is legal input — the turn did not
  change the attribute it reads.
- `voice.one-word-before-slow-work` — reclassified from needs-fixture. The old blocker said
  the acknowledgement is a separate emission the recording cannot see, but
  `ReplyChecks.WithoutAcknowledgement` already strips a leading one-word sentence precisely
  because acknowledgements land in recorded replies. The checkable half is the shape —
  `ReplyExpectation.OpensWithAcknowledgement`, new deterministic check — carried by a spoken
  search turn. The timing half (the word reaching the speaker before the tools run) is the
  channel's, and stays out of a recording's reach.
