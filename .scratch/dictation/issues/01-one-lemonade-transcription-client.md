# 01 — One lemonade transcription client

**What to build:** nothing changes for anyone using the system. This is the prefactor that
makes every ticket after it small: the multipart POST to lemonade's transcription route —
the WAV build, the model, language and prompt fields, the confidence figures parsed back
out, the timeout and error mapping — becomes one client in Infrastructure, and the voice
channel's streaming speech-to-text is reimplemented on top of it. Satellites keep behaving
exactly as they do today.

Alongside it, a domain contract for the shape the two chat channels need: complete audio
plus its media type in, a transcript with its confidence figures out. Its implementation
is the same client. The voice channel's chunk-stream contract is untouched, because its
segmenter and target-speaker decorator are built around a stream and gain nothing from a
bytes-in shape.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A transcription client in Infrastructure sends audio to lemonade and returns the text plus the confidence figures whisper reported
- [ ] A domain contract takes complete audio and its media type and returns a transcript; the Infrastructure client implements it
- [ ] The voice channel's streaming speech-to-text produces the same requests and the same results as before, now via the shared client
- [ ] Model, base URL, language, prompt and request timeout are settings, in appsettings only — no compose or `.env` entries
- [ ] The client's own tests pin the multipart form structurally (fields and file part read back), not by matching substrings in a serialized body
- [ ] A lemonade error and a request timeout each surface as something a caller can act on, distinctly
- [ ] Every existing voice speech-to-text, segmenter and prompt-builder test still passes untouched
