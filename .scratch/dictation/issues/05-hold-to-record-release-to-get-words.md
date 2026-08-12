# 05 — Hold to record, release to get words

**What to build:** the first dictation in the browser. With the composer empty, the button
on the right is a microphone instead of send. Holding it opens the microphone — permission
is asked the first time — and a recording strip replaces the textarea with an indicator and
a running timer. Letting go ends the dictation, and a moment later the words are in the
composer, where they can be read, corrected and sent like anything typed. As soon as there
is text the button is send again.

The audio is encoded in the browser as 16 kHz mono s16le WAV. This is forced, not
stylistic: whisper as lemonade runs it rejects MediaRecorder's Opus outright. The
JavaScript side owns the microphone, the encoder and the upload, and calls into the client
only at decisions — started, transcript, failed — so the recording never enters the WASM
heap. The composer state, the append rule and what the right-hand button is all live in the
client's store and effects.

Gestures, discarding, the level meter and the failure rules are tickets 06 to 08. This one
ends when a held press produces text.

**Blocked by:** 04 — A transcription endpoint the browser can reach.

**Status:** ready-for-agent

- [x] The right-hand control is a microphone while the composer holds no text and no ready attachment, and send otherwise; the streaming-cancel state is unchanged
- [x] Holding it asks for microphone permission the first time and starts recording
- [x] A recording strip replaces the textarea row while the microphone is open, with an indicator and an mm:ss timer
- [x] Releasing ends the dictation, and the transcript is appended to any existing composer text, separated by a space
- [x] The audio leaves the browser as 16 kHz mono s16le WAV, posted with a dictation ticket, and never passes through the WASM heap
- [x] The encoder lives in its own module file, since a worklet cannot be inlined in the existing script
- [x] Composer rules are unit-tested through the store and its effects, as the attachment composer tests are
- [x] One end-to-end test in the WebChat collection drives a real dictation with Chromium's fake media device and CDP touch, against an in-process listener the fixture points the channel container at
