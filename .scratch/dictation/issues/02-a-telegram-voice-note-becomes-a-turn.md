# 02 — A Telegram voice note becomes a turn

**What to build:** someone holds the microphone in Telegram, speaks, sends, and the agent
answers what they said. Today that voice note is dropped without a word and the person
watches their message go unanswered.

The channel takes the voice note, decodes it — Telegram's are Ogg/Opus, which whisper
rejects outright, so it is decoded in managed code to 16 kHz mono and handed on as WAV —
and the transcript becomes the turn's content. Nothing marks the turn as spoken: the agent
sees an ordinary turn, exactly as the satellites' transcript dispatcher already produces.
No attachment reference is minted and no bytes are kept.

Happy path only. The caption, the duration cap, the confidence floors and the failure
replies are ticket 03.

**Blocked by:** 01 — One lemonade transcription client.

**Status:** ready-for-agent

- [x] A voice note sent to the bot produces a channel notification whose content is the transcript
- [x] Ogg/Opus is decoded to 16 kHz mono s16le in managed code, with no native dependency and no change to any container image
- [x] The resulting turn carries the same sender, delivery identity and turn key a typed message would
- [x] No attachment reference is produced for a voice note, and nothing is written to any store
- [x] Audio files, documents, video notes and stickers behave exactly as they do today
- [x] Driven through the existing Telegram polling harness with a fake transcriber, asserting on the notification that reached the channel inbox
- [x] The decode is unit-tested as a pure step: a real Ogg/Opus fixture in, correct sample count and rate out
