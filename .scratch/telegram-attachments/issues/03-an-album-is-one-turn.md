# 03 — An album is one turn

**What to build:** Several photos sent together as an album become a single turn carrying every
reference and the group's caption, instead of one turn per photo with the question attached to an
arbitrary one of them.

Telegram delivers an album as separate updates sharing a media-group id, arriving as each file
finishes uploading. Messages of one group are held and released together. The window is a sliding
debounce of 1.5 seconds reset by each arrival, with no ceiling and no early release when the group
reaches Telegram's limit: a straggler on a slow upload must join its album rather than becoming a
second turn with files missing. The window only ever extends when another item of the same group
actually arrives, so a stalled or abandoned upload still releases 1.5 seconds after the last item
that did arrive.

The debounce is a constant rather than a setting. Held updates have already been acknowledged to
Telegram, so a crash inside the window loses the group; that exposure is bounded by the upload and
is accepted.

The polling service takes a time provider so the debounce is drivable in tests.

**Blocked by:** 01 — A photo or a document reaches the model.

**Status:** resolved

- [x] Photos sharing a media-group id produce one notification carrying every reference.
- [x] The group's caption becomes the turn's content, wherever in the group it arrived.
- [x] Each arrival resets the window, so an item arriving within the debounce joins its group.
- [x] A group with no further arrivals releases after the debounce, without waiting for the album to be full.
- [x] A message with no media-group id is unaffected and emits immediately.
- [x] Two interleaved groups do not merge.
- [x] Tests drive the debounce through a fake time provider rather than by waiting.
