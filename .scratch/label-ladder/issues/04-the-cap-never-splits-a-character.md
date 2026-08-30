# 04 — The cap never splits a character

**What to build:** A label that meets the length cap is cut between characters, never inside one: a label ending in an emoji or any astral character arrives whole-minus-the-tail with the ellipsis, not as a lone surrogate half. The cut becomes text-element-aware in the one ladder; the cap's value and the "a real label should never meet the cap — it is a safeguard, not an editor" stance are unchanged.

**Blocked by:** 01 — The note is named by the one ladder (the fix must land in the single implementation, once).

**Status:** ready-for-agent

- [x] A failing test first: an over-cap label whose cut point lands inside a surrogate pair currently yields a lone high surrogate.
- [x] The cut backs off to the nearest text-element boundary at or below the cap, then trims trailing whitespace and appends the ellipsis, as today.
- [x] Labels at or under the cap are byte-for-byte unchanged; the existing long-label facts still pass.
- [x] Entry and note agree on the cut label, by construction — one function, one cut.

## Comments

- 2026-08-30: Implemented on feat/label-ladder. All checklist items verified by the unit and hermetic integration suites.
