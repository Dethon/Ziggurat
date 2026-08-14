# 0026 — The speech typist talks to Lemonade directly

Status: accepted
Date: 2026-08-14

## Context

A speech typist is being added: a standalone Rust crate, Windows first, distributed as one
executable. A person holds a key, talks, and the words appear in whatever window they were
already working in. It reaches no agent, sends no message and keeps no history — its only
outward need is somewhere to turn audio into words.

That somewhere already exists and is already ours. `Infrastructure/Clients/Transcription`
holds the one place anything in this repo talks to Lemonade's OpenAI-compatible
`/audio/transcriptions` route, and it holds real knowledge that was paid for: the multipart
shape, the filename extension having to match the part's media type, `verbose_json` being
asked for unconditionally so `avg_logprob` and `no_speech_prob` reach the callers that gate
on them, and Opus being the one container whisper as Lemonade runs it cannot decode. Both
chat channels and the voice hub inject it, which is why a whisper quirk is fixed once.

So the obvious move is to make the speech typist a fourth caller: a small endpoint or
channel on the .NET side, and the desktop client posts at that. The knowledge stays in one
place and the Rust crate stays ignorant of whisper entirely.

Two facts argue the other way. Lemonade is a compose service in its own right, reachable on
its own port, and it is up whether or not anything else in the stack is. And the thing being
built is a keyboard: it is held while a person is mid-sentence in someone else's
application, and it has no conversation to fall back into when it cannot work.

## Decision

**The speech typist posts at Lemonade itself. It does not route through the .NET stack, and
it re-implements the multipart contract in Rust.**

The endpoint and model are configuration, defaulting to the Lemonade host and the model the
container is warmed with. Nothing in `Ziggurat.sln` is referenced, built or required for it
to run.

The duplication is bounded on purpose rather than mirrored: the Rust side sends WAV and
nothing else. It captures its own audio, so there is no sender-supplied media type to
distrust, no container to sniff and no Opus to decode — the three problems
`AudioContainer` exists to solve are problems the speech typist does not have. What is
genuinely duplicated is the multipart shape, `verbose_json`, and reading the two quality
signals back out.

## Considered options

**A new endpoint on the .NET side wrapping `IAudioTranscriber`.** One implementation of the
whisper contract, and a quirk found later is fixed for the speech typist too. Rejected
because it makes a person's keyboard depend on the agent stack being up. A hub that is
restarting, mid-deploy or simply not running today takes the ability to type with it, for
no functional gain — the request would be forwarded unchanged to a Lemonade that was
reachable all along. It also wants authentication, a route and a settings section for a
client that is not part of the system it would be talking to.

**Reuse the .NET client by writing the speech typist in .NET.** Genuinely removes the
duplication rather than relocating it. Rejected on the client's own terms: tray, low-level
keyboard hook and `SendInput` are P/Invoke either way, so the shared language buys much less
than it looks like, and it trades a 4–6 MB single executable for a NativeAOT publish.

**Route through the voice hub as a satellite.** The wire protocol and the segmenting already
exist there. Rejected because a satellite is a room with a microphone in it and this is a
keyboard: it has no wake word, no speaker, no arbitration and no conversation, so almost
nothing the protocol carries applies, and the parts that do would be reimplemented anyway.

## Consequences

- The whisper contract now exists twice, in two languages, and a Lemonade-side change can
  break one while the other keeps working. This is the cost that was accepted. What limits
  it is that the Rust side is deliberately the poorer of the two: WAV only, no sniffing, no
  decoding, no streaming.
- The model name now has a fifth place to agree with `STT_MODEL`. The compose file already
  keeps four in lockstep and says why; the speech typist's config is outside compose and
  cannot be pinned by the same anchor, so a model override has to be repeated there by hand.

  **Corrected 2026-08-14, after the first run on real hardware.** This originally read "a
  mismatch is not an error ... Lemonade lazily pulls whatever it was asked for, and the symptom
  is a slow first dictation, not a failure". That is wrong, and wrong in the direction that
  matters: Lemonade holds one transcription model at a time, the deployed one is **pinned**, and
  a request naming any other model is refused with `409 slots_pinned_error` — "All loaded models
  of type transcription are pinned. Unload a model first." Nothing is transcribed and nothing is
  typed. The prod instance answers `Whisper-Large-v3-Turbo-ES`, the Spanish fine-tune, which is
  not compose's `${STT_MODEL:-Whisper-Large-v3-Turbo}` fallback either. So this is a hard
  coupling to a running server's state, not a soft one — which strengthens rather than weakens
  the decision below to accept the duplication, but it is a sharper cost than was signed up for.
- The Lemonade URL is a per-deployment value in a file next to the executable. The
  compose-internal `lemonade:13305` is meaningless from a Windows desktop, so the default
  names the host.
- The quality-signal gate is duplicated too, and deliberately: a segment above the
  `no_speech_prob` threshold is dropped rather than typed, because whisper's hallucinations
  on near-silence would otherwise be injected into a person's document. It fails open on a
  missing signal for the same reason the .NET side does.
- The speech typist works with the whole rest of the stack down, which is the point. It does
  not work with Lemonade down, and there is no fallback — no local model is bundled and none
  is planned.
