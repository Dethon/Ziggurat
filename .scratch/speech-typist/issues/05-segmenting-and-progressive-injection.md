# 05 — Segmenting and progressive injection

**What to build:** words arrive while you are still talking. Dictate a paragraph and it reads onto
the screen phrase by phrase at roughly the speed you speak, instead of all at once when you let go
of the key.

A dictation is cut into segments wherever you pause. An energy detector with hysteresis watches the
incoming frames and cuts after roughly 400 ms below the speech threshold, keeping roughly 200 ms of
padding either side of the cut so a leading plosive is not clipped off the next segment. If no pause
has appeared for 20 s — you are monologuing — a cut is forced at the lowest-energy point in the
preceding second, so the split lands in a gap between words and every segment stays inside Whisper's
30 s window. Every threshold and window here is a config key, not a constant.

Segments are transcribed one at a time, strictly in order. That is what removes any need for a
reorder buffer, and ticket 06 depends on it. Each transcript is trimmed of surrounding whitespace,
and every segment after the first in a dictation is prefixed with a single space. Nothing else is
rewritten: Whisper already punctuates and capitalises, and a rewrite layer would fight it.

The detector sits behind its own small interface so a model-based one can replace it later without
touching the core — but no model-based detector is written here.

`satellite/src/audio/room.rs` is the model for these tests: feed synthetic PCM to a pure unit, name
each test after the behaviour it pins, and where a threshold exists because of a real observation,
say so in a comment.

**Blocked by:** 04.

**Status:** resolved

- [x] A pause in speech produces a cut, and the padding either side of it is retained
- [x] Speech with no pause for 20 s is force-cut at a low-energy point, inside Whisper's window
- [x] Segments are transcribed one at a time and injected strictly in order
- [x] Each transcript is trimmed; every segment after the first is prefixed with one space
- [x] Nothing else about the transcript is altered
- [x] A dictation with no speech in it still injects nothing
- [x] Thresholds and windows are config keys with sensible documented defaults
- [x] The detector is replaceable without changing the core
- [x] All of the above is tested through the fake host and the pure detector, with no Windows

## Comments

2026-08-14 — Done. One detail worth recording: the padding either side of a cut is not
symmetrical machinery. The trailing side is literal — the segment keeps ~200 ms of the pause
so a final consonant is not clipped. The leading side is a rolling pre-roll that a long pause
cannot grow, because retaining the pause itself would hand whisper a segment that is mostly
silence and invite the very hallucination ticket 08 exists to catch.
