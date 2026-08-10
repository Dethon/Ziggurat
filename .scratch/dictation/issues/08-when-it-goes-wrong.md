# 08 — When it goes wrong

**What to build:** every way a dictation can end badly, answered in plain words rather than
by nothing happening.

Someone who refuses microphone permission, or whose browser cannot record at all, gets one
short line in the composer's existing refusal slot and a control that stops trying for the
session. A transcription that fails shows the same one-line refusal; the audio is gone, so
the only way on is to record again. While a transcript is in flight the composer is busy
and send is unavailable, reusing the notion of busy that an upload in flight already has,
so the half-message the transcript was meant to complete cannot go out without it.

Leaving the topic or hiding the tab stops the microphone and drops the audio, so no
recording outlives the screen it started on and no background tab quietly holds the
microphone open. The live connection matters only when the ticket is minted and the request
made — recording through a network gap is fine, and the failure is the ordinary one.

**Blocked by:** 05 — Hold to record, release to get words.

**Status:** ready-for-agent

- [x] A denied permission shows a one-line refusal and disables the control for the session
- [x] A browser without the required APIs shows the same refusal rather than a dead control
- [x] A failed transcription shows the refusal, and no partial text lands in the composer
- [x] Send is unavailable while a transcription is in flight and returns when the text arrives
- [x] Switching topic mid-dictation stops the microphone and discards; nothing arrives in the new topic
- [x] Hiding the tab stops the microphone and discards
- [x] Losing the connection mid-recording does not stop the recording; only the request fails, as an ordinary refusal
- [x] Rules covered through the store and effects, with the refusal path also asserted end-to-end against a listener answering an error
