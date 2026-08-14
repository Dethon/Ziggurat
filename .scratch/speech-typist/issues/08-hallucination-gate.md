# 08 — Hallucination gate

**What to build:** near-silence never gets typed. Whisper hallucinates on quiet audio — a stock
"Thank you.", subtitle credits — and without this the fan, the air conditioning and a mechanical
keyboard put words into your document that nobody said.

The client already reads back the duration-weighted `avg_logprob` and `no_speech_prob` from
`verbose_json`. A transcript above the `no_speech_prob` threshold (default around 0.6) or below the
`avg_logprob` threshold (default around -1.0) is dropped rather than injected. Both thresholds are
config keys.

A signal that is absent or malformed means no signal, and the transcript is injected anyway — fail
open, for the same reason the .NET client fails open. A shortcoming in the response must never
silently swallow words that were actually said.

Dropping a segment is not an error and raises nothing: it is the gate working. Ticket 09 covers what
happens when a segment fails for real.

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] A transcript above the `no_speech_prob` threshold is dropped and nothing is injected
- [ ] A transcript below the `avg_logprob` threshold is dropped
- [ ] A transcript with either signal absent or malformed is injected
- [ ] A dropped segment reports no error and leaves the tray in its normal state
- [ ] A drop mid-dictation does not disturb the ordering or joining of the segments around it
- [ ] Both thresholds are config keys with documented defaults
- [ ] Covered through the fake host, including the fail-open cases
