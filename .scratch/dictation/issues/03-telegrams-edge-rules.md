# 03 — Telegram's edge rules

**What to build:** the cases around the happy path, so that a person is never left guessing.
A voice note sent with a caption keeps both: the caption, a newline, then the transcript,
because both are things they said. A voice note too long to be worth transcribing is
refused before its bytes are downloaded. A voice note the system cannot make out — empty,
below the confidence floors, an undecodable container, or a transcriber that failed —
gets a short reply saying so, instead of today's silence, and opens no turn.

The container is decided by sniffing the leading bytes rather than by the reported MIME
type, which the Bot API documents as optional and sender-supplied. Ogg/Opus is decoded;
WAV, MP3, FLAC and Ogg/Vorbis are forwarded untouched because whisper decodes those itself;
anything else is refused.

**Blocked by:** 02 — A Telegram voice note becomes a turn.

**Status:** ready-for-agent

- [ ] A captioned voice note produces one turn whose content is the caption, a newline, then the transcript
- [ ] A voice note longer than the configured cap is refused with a short reply, and its file is never downloaded
- [ ] An empty transcript, or one below the configured confidence floors, produces the could-not-understand reply and no turn
- [ ] A transcriber failure produces the same reply and no turn
- [ ] The container is chosen by the leading bytes; a reported MIME type that disagrees with them is ignored
- [ ] WAV, MP3, FLAC and Ogg/Vorbis voice notes are forwarded to whisper without local decoding; an unrecognised container gets the reply
- [ ] The cap and the floors are settings, in appsettings only
- [ ] All of it asserted through the polling harness on what the person sees — the notification, or the message the bot sent back
