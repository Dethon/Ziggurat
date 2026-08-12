# 07 — Recording feedback

**What to build:** enough on screen that a person knows what the microphone is doing.

Bars driven by an analyser node move with their voice, so a muted or misrouted input device
is obvious while they are still speaking rather than after the transcript comes back empty.
The slide-to-discard hint travels and fades with the pointer, so they can tell how far they
have to go before the recording is dropped. A press shorter than the mis-tap floor records
nothing and shows a short hint telling them to hold it. A dictation that runs past the cap
stops itself and transcribes what it has, so a pocketed phone cannot record indefinitely.

The cap and the floor come from the server's limits, not from constants in the client.

**Blocked by:** 05 — Hold to record, release to get words.

**Status:** ready-for-agent

- [x] The strip shows a level meter that responds to input, driven by an analyser node alongside the encoder
- [x] The slide-to-discard hint moves and fades in proportion to how far the pointer has travelled
- [x] A press shorter than the mis-tap floor records nothing, sends no request, and shows a hold-to-record hint
- [x] A dictation reaching the cap stops itself and transcribes what it has
- [x] Both numbers come from the server's limits, so changing them needs no client deploy
- [x] The strip stays legible at a phone viewport and does not change the composer's height
