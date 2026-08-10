# 06 — Latch and discard

**What to build:** the two things a person does with a dictation other than finish it,
and the way in for anyone not holding a pointer down.

Sliding the finger toward the textarea throws the recording away: no transcript, no
request, nothing in the composer. Sliding it up latches the dictation — the same dictation,
the same clock, the same audio still accumulating, with only the way it can end changing
from letting go to pressing. A latched dictation shows a trash button that discards
immediately with no confirmation, and a stop button that ends it and fills the composer.
That button never sends: nothing is ever sent that the person has not read, whichever way
the dictation was held, so it should not wear a paper plane.

A plain click, or Enter or Space on the focused microphone, starts a latched dictation
straight away. Escape discards.

Geometry mirrors the hearth sheet's proven numbers: 8 px before a direction is committed,
latch at 56 px of upward travel, discard at 96 px toward the textarea, 24 px of tap slop so
a drifting press still counts as a hold.

**Blocked by:** 05 — Hold to record, release to get words.

**Status:** ready-for-agent

- [ ] Sliding toward the textarea past the threshold discards: no transcript request is made and the composer is untouched
- [ ] Sliding up past the threshold latches; releasing the pointer afterwards does not end the dictation
- [ ] A latched dictation offers a trash button that discards immediately and a stop button that ends it and fills the composer
- [ ] The stop button never sends the message
- [ ] A click, Enter or Space on the focused microphone starts a latched dictation; Escape discards it
- [ ] A press that drifts within the tap slop still counts as a hold, not a mis-tap
- [ ] End-to-end coverage over CDP touch for slide-to-discard and for latch-then-stop
