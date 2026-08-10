# 09 — Dictation on the metrics dashboard

**What to build:** an operator can see how transcription is behaving for the channels people
type into, not only for the satellites. When whisper gets slow after a model change, or
starts failing, it shows up on the dashboard the same day.

Dictation records the existing voice speech-to-text metric members — latency, errors and
transcribed utterances — from both call sites. Their values are pinned and must not be
renumbered. The channel is already a dimension, so the dashboard separates satellite, chat
and Telegram without a new metric family.

**Blocked by:** 02 — A Telegram voice note becomes a turn; 04 — A transcription endpoint the
browser can reach.

**Status:** ready-for-agent

- [ ] A dictation from either channel records transcription latency and a transcribed-utterance event
- [ ] A failed transcription records the error member
- [ ] Existing metric member values are unchanged; no new family is introduced
- [ ] Breaking down by channel separates satellite, chat and Telegram
- [ ] Publishing stays fire-and-forget: a metrics failure never affects a dictation
