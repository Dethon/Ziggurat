# 05 — The dead reply kind goes, the preamble goes live

**What to build:** Two moves in one slice, re-wire first so the deleted tests have somewhere to land. First: the reply speaker gains one public preamble entry taking the session and the conversation id — it claims the turn's preamble exactly once, speaks under the preamble playback kind at normal priority, and never touches the turn handshake (ADR-0013 and ADR-0003 both hold) — and the voice approval tool's notify branch calls it instead of its hand-rolled unguarded flush. The observable change is deliberate: the spoken acknowledgement before slow tool work arrives once per turn, first tool call wins, mid-run narration stays buffered for the answer. Second: the tool-call reply kind is deleted entirely — the enum member, the formatter, all four channel case arms, and the test references — completing ADR-0018. Verified safe in the grilling: zero producers, string-named wire, one compose build, no persistence, no outpost exposure; a consuming arm without a producer is dead code, so the deletion is full, Telegram's arm included.

**Blocked by:** None — can start immediately. (Touches the same Telegram reply tool as ticket 04 — coordinate merge order, no logical gate.)

**Status:** ready-for-agent

- [ ] Red-first: a voice approval-tool test asserts the notify branch speaks buffered text once per turn under the preamble playback kind — it fails today (every call speaks, approval kind).
- [ ] The seven reply-speaker preamble tests migrate to the public entry point, asserting the same contracts: no turn resolution, separate utterances, nothing spoken on an empty buffer, second tool call keeps narration buffered, dispatch stamp survives.
- [ ] The redial test that used the dead kind incidentally is re-expressed without it, asserting the same settle behaviour.
- [ ] The enum member, the formatter, and all four channel arms are gone; no reference to the dead kind remains anywhere.
- [ ] The live tool-call rendering path (the approval flow's own formatting) is untouched and its tests green.
- [ ] Full unit suite green with no suppressions added.
