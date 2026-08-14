# 02 — The core, the host port, and the Lemonade client

**What to build:** everything about the speech typist that does not need Windows, provable entirely
in WSL with `cargo test`. After this ticket a dictation can be driven from start to finish against a
fake host and a fake Lemonade, and the only thing missing is a real keyboard, a real microphone and
a real window.

The crate lives at `speech-typist/`, outside `Ziggurat.sln`, with its own manifest, lockfile,
toolchain file and release profile — the `satellite/` shape, sharing no build with it. It must build
and test with no part of the .NET solution present and no Lemonade reachable.

The **host port** is the one seam in this crate. It is a trait the core holds, carrying the outward
operations — inject a transcript, report the identity of the window currently in front, set the tray
state, play a cue, transcribe a stretch of audio — and nothing else. Inward events (a binding
pressed, a binding released, a frame of microphone audio, a timer elapsing) arrive on a channel that
a real host will later fill and that a test fills directly. Write the fake host here: it records
what it was asked to do and answers with what the test told it to answer.

The core is the dictation itself: a binding goes down, frames accumulate, the binding comes up, the
audio becomes one WAV, that WAV becomes a transcript, and the transcript is injected. One segment
per dictation for now — cutting at pauses is ticket 05. A dictation in which nothing was said
injects nothing and is not an error.

The **Lemonade client** posts at the OpenAI-compatible `/audio/transcriptions` route: a multipart
body whose audio part is named with a `.wav` extension because whisper-server refuses a part it
cannot name, `response_format` always `verbose_json`, and the model and language sent as fields. It
reads back the text and the duration-weighted `avg_logprob` and `no_speech_prob`, degrading to no
signals rather than an error when a body carries no segments. Only WAV is ever sent — the speech
typist captures its own audio, so there is nothing to sniff and no Opus to decode. This is the
contract `docs/adr/0026` knowingly duplicates from the .NET side, so it is pinned by a test against
a fake Lemonade on localhost that asserts on the bytes actually sent.

The endpoint and model come from the environment for now; the config file is ticket 04.

**Blocked by:** None — can start immediately (independent of 01, which only de-risks 03).

**Status:** ready-for-agent

- [ ] The crate builds and `cargo test` passes with no .NET project built and no Lemonade reachable
- [ ] A single host port trait carries every outward operation; there is no second seam
- [ ] Inward events arrive on a channel a test can fill directly
- [ ] A fake host records injections, tray states and cues, and answers transcription as scripted
- [ ] Driving a binding down, frames, binding up through the fake host injects the transcript
- [ ] A dictation with no speech in it injects nothing and reports no error
- [ ] A loopback fake Lemonade test asserts the multipart shape, the part's `.wav` filename, and the
      `model`, `language` and `response_format` fields as actually sent
- [ ] `verbose_json` parsing returns the text and the duration-weighted quality signals
- [ ] A response body with no segments yields no signals rather than an error
- [ ] A request timeout and a 500 surface as distinguishable errors
