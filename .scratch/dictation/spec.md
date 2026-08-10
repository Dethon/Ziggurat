# Dictation

Status: ready-for-agent

## Problem Statement

Talking is faster than typing, and on a phone it is far faster. Today neither channel a
person actually chats through will take spoken words.

In WebChat there is no microphone at all: the composer offers a textarea and a file
picker, and a thought that would take four seconds to say takes half a minute to thumb in.

In Telegram it is worse than absent, because it looks broken. A voice note is classified
as something the person performed rather than a file they attached, and is then dropped
without an attachment and without a refusal — the channel emits nothing at all, so the
person watches their message sit there unanswered and cannot tell whether the bot ignored
them, failed, or is still thinking.

Meanwhile the system already transcribes speech continuously: the voice satellites run
every utterance through whisper on lemonade. The capability is in the building and the two
channels people actually type into cannot reach it.

## Solution

**Dictation**: one run of a microphone that ends as words. Both channels gain it, and in
neither does the audio become a message. The recording exists only until the transcript
comes back and is then gone — no playback bubble, no audio attachment kind, no bytes in
the upload store, nothing in a conversation's history but text.

In WebChat the microphone takes the send button's place while the composer is empty, and
behaves the way people already expect from WhatsApp and Telegram: hold to record, slide up
to latch, slide toward the textarea to discard. When the dictation ends the transcript lands
in the composer, where it can be read, corrected and sent like anything typed. The person
is always the one who presses send.

In Telegram there is no composer to land in, so a voice note becomes the turn's text
directly and the agent answers it as if it had been typed. If the words cannot be made out,
the channel says so instead of staying silent.

## User Stories

1. As someone chatting in WebChat on a phone, I want to hold a microphone button and speak, so that I can say a long thought without thumbing it in.
2. As someone chatting in WebChat, I want the microphone to sit where the send button sits when I have typed nothing, so that I do not lose composer width to a third button.
3. As someone who has started typing, I want the send button back the moment there is text, so that the control under my thumb always does the thing I am about to do.
4. As someone dictating, I want to see that recording has actually started, so that I do not talk into a microphone that never opened.
5. As someone dictating, I want a running timer, so that I know how long I have been speaking.
6. As someone dictating, I want to see that the microphone is hearing me, so that I find out about a muted or misrouted input device while I am speaking rather than after.
7. As someone dictating, I want to slide my finger away from the button to throw the recording away, so that I can abandon a sentence I have fumbled without sending anything.
8. As someone sliding to discard, I want the hint to move and fade as I go, so that I can tell how far I have to travel before the recording is dropped.
9. As someone dictating a long thought, I want to slide up to latch the recording, so that I can stop holding the button down and keep talking.
10. As someone with a latched dictation, I want a trash button that drops it immediately, so that abandoning it takes one press and no confirmation.
11. As someone with a latched dictation, I want a button that ends it and gives me the words, so that a latched dictation finishes the same way a held one does.
12. As someone who taps the microphone by accident, I want nothing to be recorded and a short hint telling me to hold it, so that a mis-tap does not produce an empty message.
13. As someone who forgets to stop, I want recording to end by itself at two minutes, so that a pocketed phone cannot record indefinitely.
14. As someone at a desk, I want hold-and-release to work with a mouse, so that the feature is not phone-only.
15. As someone using a keyboard, I want a plain click or Enter on the microphone to start a latched dictation, so that I never have to hold a key down.
16. As someone using a keyboard, I want Escape to discard, so that I can abandon a dictation without reaching for the trash button.
17. As someone who has just dictated, I want the transcript in the composer rather than sent, so that I can fix what whisper misheard before the agent sees it.
18. As someone who half-typed a message and then spoke the rest, I want the transcript appended to what I typed, so that dictating never destroys my words.
19. As someone waiting on a transcript, I want send to be unavailable until it arrives, so that I cannot accidentally send the half-message the transcript was meant to complete.
20. As someone whose transcription failed, I want a plain one-line explanation in the composer, so that I know to record it again rather than wonder where my message went.
21. As someone who has never used the microphone here, I want to be asked for permission when I first press it, so that I am not prompted on page load for something I may never use.
22. As someone who has refused microphone permission, I want to be told why the button will not work, so that I do not press a dead control repeatedly.
23. As someone on a browser that cannot record, I want the same clear message rather than silence, so that I stop trying.
24. As someone who switches topic mid-dictation, I want the recording dropped, so that words meant for one conversation never surface in another.
25. As someone who switches away from the tab, I want recording to stop, so that a background tab is never quietly holding my microphone open.
26. As someone whose connection dropped, I want the recording itself to keep working and only the transcription to fail, so that a brief network gap is a retry and not a lost sentence.
27. As someone messaging the agent from Telegram, I want to send a voice note and get an answer, so that I can use the bot while walking.
28. As someone sending a Telegram voice note with a caption, I want both to reach the agent, so that the note I typed alongside is not thrown away.
29. As someone whose Telegram voice note could not be understood, I want a short reply saying so, so that I can tell the difference between being misheard and being ignored.
30. As someone who sends a very long Telegram voice note, I want to be told it was too long, so that I do not wait for an answer that will never come.
31. As someone who sends an ordinary music file to Telegram, I want the existing refusal rather than a transcription attempt, so that the bot's behaviour with attachments does not change under me.
32. As the agent, I want a dictated turn to look exactly like a typed one, so that nothing about how the words arrived changes how I answer.
33. As the agent, I want a Telegram voice note to arrive as a normal turn with sender and delivery identity intact, so that memory and history work as they always have.
34. As an operator, I want dictation latency and errors on the existing metrics dashboard split by channel, so that I can see when transcription gets slow or starts failing.
35. As an operator, I want the recording cap, the transcription language and the confidence floors to be settings, so that I can tune them without a client deploy.
36. As an operator, I want the browser to learn the cap from the server, so that changing it does not require shipping new JavaScript.
37. As a maintainer, I want one transcription adapter shared by every channel, so that a whisper quirk is fixed in one place.
38. As a maintainer, I want the voice channel's streaming pipeline left alone, so that adding dictation cannot regress the satellites.

## Implementation Decisions

### The shared transcriber

- A new contract in the domain takes complete audio plus its media type and returns a
  transcript, with the implementation living in Infrastructure alongside the other adapters.
  It is the single injection point for both channels.
- The voice channel keeps its existing streaming speech-to-text contract unchanged. Its
  segmenter and TSE decorator are built around a chunk stream and gain nothing from a
  bytes-in shape; both contracts sit on the same lemonade HTTP client underneath.
- The transcript carries the text plus the confidence figures whisper reports, because the
  Telegram path gates on them and WebChat does not.
- Model, base URL, language, prompt and the confidence floors are settings. Language
  defaults to Spanish. Chat dictation gets its own settings section rather than sharing the
  voice channel's, whose short-phrase prompt and room placeholders are tuned for satellite
  commands and would skew long-form dictation.
- Per the repo's configuration rule these are generic tunables and belong in appsettings
  alone — no compose entries, no `.env` placeholders.

### Audio formats

- whisper-server v1.8.4 as lemonade runs it decodes in memory through miniaudio plus
  stb_vorbis: **WAV, MP3, FLAC and Ogg/Vorbis are accepted; Ogg/Opus, WebM/Opus and M4A/AAC
  return `400 Invalid request`.** The lemonade image contains no ffmpeg, and whisper.cpp's
  ffmpeg fallback exists only on its file-path branch, not the in-memory one the server uses.
  This was verified empirically against the running instance, not inferred.
- WebChat therefore encodes 16 kHz mono s16le WAV in the browser rather than using
  MediaRecorder, whose output is Opus in a container whisper rejects. This is the same
  format the voice channel already hand-builds for satellite audio.
- Telegram voice notes are normally Ogg/Opus and must be decoded before whisper sees them.
  Concentus plus Concentus.Oggfile do it in managed code and bundle the speexdsp resampler
  for 48 kHz to 16 kHz. Both restore on net10.0 with no native dependency, which is why they
  win over adding ffmpeg to the channel image or standing up a decode sidecar.

### WebChat composer

- The right-hand control is a microphone while the composer holds no text and no ready
  attachment, and the existing send button otherwise. The streaming-cancel state is
  unchanged and continues to take precedence.
- A recording strip replaces the textarea row while the microphone is open: a pulsing
  indicator, an mm:ss timer, a level meter driven by an analyser node, and a slide-to-discard
  hint that travels and fades with the pointer. A latched dictation swaps the hint for a
  trash button and a stop button.
- Gesture geometry mirrors the hearth sheet's proven numbers: 8 px before a direction is
  committed, latch at 56 px of upward travel, discard at 96 px toward the textarea, 24 px of
  tap slop so a drifting press still counts as a hold.
- A press shorter than 400 ms records nothing and shows a hold-to-record hint. Recording
  stops itself at 2 minutes and transcribes what it has.
- A latched dictation's stop button ends it and fills the composer. It never sends. Nothing
  is ever sent that the person has not read, whichever way the dictation was held, and the
  icon should not be a paper plane.
- The transcript is appended to any existing composer text, separated by a space. Send is
  unavailable while a transcription is in flight, reusing the existing upload-in-flight
  notion of a busy composer rather than introducing a second one.
- Leaving the topic or hiding the tab discards. The live connection matters only when the
  ticket is minted and the request is made; a failed request is the existing one-line
  composer refusal, and the audio is gone with it.
- Microphone permission is requested on first press. A denial, or a browser without the
  APIs, shows the same one-line refusal and disables the control for the session.

### Browser and .NET split

- A dictation namespace in the client's JavaScript owns the microphone, the encoder, the
  drag thresholds and the upload, and calls into .NET only at decisions — started, latched,
  discarded, transcript, failed. This mirrors how the hearth sheet registers and reports.
- The encoded audio never enters the WASM heap: JavaScript posts it directly with a ticket
  the .NET side hands over, and returns only the transcript string.
- Because a worklet cannot be inlined in the existing script, the encoder needs its own
  module file.
- .NET owns composer state: an effect handles the reported transitions and the transcript,
  the composer slice carries the dictation state, and the selectors decide what the send
  control is and whether it is available.

### Chat channel server

- A transcription endpoint alongside the existing attachment endpoints, taking one audio
  file and a ticket header and answering with the transcript. Nothing is written to the
  upload store, no reference is minted, and the sweeper and retention rules never see it.
- The ticket store grows a dictation ticket beside its upload and download tickets: same
  expiry and pruning, space-scoped rather than topic-scoped, and with no slot counting. A
  dictation produces composer text, so it must not force a conversation into existence the
  way picking a file does, and it must not consume one of a message's attachment slots.
- Minting requires a registered user, matching the rule the download ticket now follows.
- The existing limits hub call grows the recording cap and the mis-tap floor, so the client
  learns them where it already learns the attachment rules, and changing them needs no
  client deploy.
- The endpoint enforces the cap on its own rather than trusting the browser.

### Telegram channel server

- Only voice notes are transcribed. Audio files, documents and video notes keep today's
  behaviour exactly, including the existing refusal text for a file the agent cannot read.
- The duration Telegram reports is checked before the file is downloaded, against the same
  2 minute cap WebChat uses. Over it, the person gets a short reply and no turn opens.
- The container is decided by sniffing the leading bytes, not by the reported MIME type,
  which the Bot API documents as optional and sender-supplied. Ogg/Opus is decoded in
  process; WAV, MP3, FLAC and Ogg/Vorbis are forwarded untouched because whisper decodes
  them itself; anything else gets the could-not-understand reply.
- A caption is kept: the turn's content is the caption, a newline, then the transcript. With
  no caption it is the transcript alone. Nothing marks the turn as spoken — the agent sees
  an ordinary turn, exactly as the satellites' transcript dispatcher already produces.
- A transcript that is empty or falls below the confidence floors becomes the
  could-not-understand reply rather than a turn. WebChat is deliberately not gated, because
  the person there reads the words before sending and the floors would only delete text they
  can see is right.

### Observability

- Dictation records the existing voice STT metric members — latency, errors, and
  transcribed utterances. Their values are pinned and must not be renumbered. The channel is
  already a dimension, so the dashboard separates satellite, chat and Telegram for free.

### Deployment

- The reverse proxy routes attachment paths explicitly per space host; the transcription
  path needs its own route block for each host, or it will fall through to the web client.

## Testing Decisions

Red-Green-Refactor throughout: a failing test first, watched failing, then the
implementation. A good test here asserts what a person or the agent would observe — the
notification that reached the channel inbox, the reply the bot sent, the text in the
composer, the bytes on the wire to lemonade — and never the intake, the decoder or the
state machine as surfaces in their own right.

**Seams.** Existing ones, wherever possible. The test double sits at the transcriber
contract for channel tests, so a Telegram or endpoint test fails only for reasons about
Telegram or the endpoint. The adapter's own behaviour against lemonade is pinned once, at
the HTTP boundary.

1. **The transcription adapter**, against a stubbed message handler, in the style of the
   existing OpenAI speech-to-text tests: the multipart shape, the file part, model, language
   and prompt fields, the confidence figures parsed back out, the timeout surfaced as a
   timeout, and a lemonade error mapped to a failure the channels can act on. Assert the
   form structurally rather than by matching substrings in a serialized body — the existing
   suite documents why.
2. **The Opus decode**, as a pure unit: a real Ogg/Opus fixture in, 16 kHz mono s16le out,
   correct sample count and no drift; plus the sniffing rules — Opus decoded, WAV/MP3/FLAC/
   Vorbis passed through, an unknown container refused, and a reported MIME type that
   disagrees with the bytes ignored.
3. **The Telegram channel**, through the existing polling harness — a real update with a
   voice note driven through the real polling service against the mocked bot client, with a
   fake transcriber. Cases: transcript becomes the turn's content; caption then newline then
   transcript; over-duration refused before download with a reply and no notification; a
   low-confidence or empty transcript produces the reply and no turn; a transcriber failure
   produces the reply; an audio file or document still gets today's refusal; a voice note
   still produces no attachment reference.
4. **The transcription endpoint**, hosted over the test server as the attachment endpoint
   tests do, with a fake transcriber: a valid ticket returns the transcript, an unknown,
   expired or wrong-space ticket is refused, an unregistered caller cannot mint one,
   oversized audio is refused, and nothing lands in the upload store in any case.
5. **The composer**, through the store and its effects, as the attachment composer tests do:
   the control is a microphone when empty and send when not, a reported transcript is
   appended to existing text, send is unavailable while transcribing, a failure surfaces as
   the one-line refusal, and a topic change discards.
6. **End to end**, in the WebChat E2E collection with the existing fixture and collection,
   one new feature class. Chromium is launched with the fake media device and auto-granted
   permission flags, and gestures are dispatched over CDP touch as the hearth flick tests do
   — synthetic pointer events are untrusted and never enter the input pipeline. The fixture
   starts a small in-process HTTP listener answering the transcription route and points the
   channel container at the host gateway, so the test controls the answer per case and no
   model or GPU is involved. Cases: hold, release, transcript appears in the composer and
   the person sends it; slide away and nothing appears; latch, stop, transcript appears;
   a stub 500 shows the composer refusal.

## Out of Scope

- Playing audio back anywhere. There is no audio attachment kind, no voice bubble, and no
  audio in a conversation's history.
- Transcribing Telegram audio files, documents, video notes or forwarded media. Their
  current behaviour is unchanged.
- Transcribing anything already in a conversation's history, or retro-transcribing voice
  notes the channel dropped before this feature existed.
- Speaking replies back in WebChat. Text to speech remains a satellite concern.
- Speaker verification, target-speaker extraction and the noise-floor machinery. Those
  belong to the satellite pipeline and have no meaning for a browser or a phone recording.
- Streaming or partial transcripts. A dictation is transcribed once, when it ends.
- Wake words, follow-up turns, or any hands-free conversation loop in the browser.
- Dictation in the metrics dashboard client.
- Changing the voice channel's streaming speech-to-text contract or its decorators.

## Further Notes

- The empirical format matrix came from posting real encoded audio at the live lemonade
  instance on the user's own host. A tone-only WAV fails differently from a rejected
  container — the voice activity detector strips it and the server errors on a null string —
  so a probe must use real speech to distinguish "cannot decode" from "decoded, heard
  nothing".
- Whisper Medium is what the voice channel asks for; the instance also has Large-v3-Turbo
  downloaded and pulls models lazily, so the model is worth leaving as a setting rather than
  a constant.
- `getUserMedia` needs a secure context. WebChat is served over HTTPS with real certificates,
  so this is satisfied in deployment — but not on a plain-HTTP LAN address, which is worth
  remembering when testing by hand.
- The glossary defines Dictation, Transcript and Latched dictation. Use them: a dictation is
  never a "voice message", and a latched one is not "locked" or "hands-free". Throwing a
  recording away has no pinned name, so say what happens rather than reaching for "cancel",
  which already belongs to stopping a turn.
- No ADRs were requested for this feature; the decisions live here and in the glossary.
