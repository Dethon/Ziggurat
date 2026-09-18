# 03 — A turn with nothing lasting in it is not extracted

**What to build:** The gate. In `MemoryExtractionWorker`, after the window is built, the three nouls (`fact`, `preference`, `instruction`, worded as in the probe script) are asked through the typed-judgment contract over the window as fields — `context` and `current`, never the rendered string. Every answer at or below `memory.judgments.gate.skipAtOrBelow` skips the extractor and publishes `Outcome = gated`; anything else, including no answer, extracts as today. One `MemoryJudgmentEvent` per call. Settings in `Agent/appsettings.json` alone.

**Blocked by:** 01, 02, and `.scratch/jev-skill-preload/issues/02`

**Status:** resolved

- [x] With a fake contract answering 0.05 to all three, the extractor is not called and the event says `gated`.
- [x] With any one answer above the bar, or the contract answering absence, the extractor is called exactly as before.
- [x] `judgments.enabled: false` asks nothing.
- [x] The state sent holds the window's text turns as `context` and the current message as `current`, with no tool content — asserted on the request the fake received.
- [x] A judgment event is published for an answered and for an absent call, saying which.
- [x] Spec: `.scratch/jev-memory-judgments/spec.md` § A — the gate.
