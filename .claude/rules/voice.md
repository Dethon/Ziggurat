---
paths:
  - "McpChannelVoice/**"
  - "satellite/**"
  - "scripts/provision-satellite-rs.sh"
  - "scripts/wsl-satellite*.sh"
  - "scripts/enroll-voice.sh"
---

# Voice Satellite Architecture

Voice is an MCP channel server (`McpChannelVoice`, channelId `voice`, container `mcp-channel-voice`, port 6015) plus hardware satellites. It registers through `AddChannelServer(DeliveryPolicy.Broadcast)` (`Mcp.Hosting`): an idle-but-unpruned subscriber still receives, so a brief agent gap does not cost the user a repeated utterance. `TranscriptDispatcher` emits through the shared `ChannelNotificationEmitter`, putting `Location`, `SatelliteId` and `DismissedAlert` on the payload as named properties. The hub is the Wyoming **client**: `WyomingSatelliteHost` dials every satellite with an `Address` in `VoiceSettings.Satellites` (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) and reconnects forever; address-less satellites stay in the catalog as announce targets but are never dialed (announcements report offline).

Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → **speaker-verification gate** (`Services/Verification`, ONNX embeddings scored against profiles enrolled under `/voices` via `scripts/enroll-voice.sh`; non-enrolled audio is dropped pre-STT, a conclusive match routes the speaker's folder name into the message sender for per-person memory) → optional **TSE** (`Services/Tse`, an STT decorator calling the `tse-extractor` container; `Tse__Mode` ∈ Off|Auto|Always, Auto gated by `NoiseFloorThreshold`) → **Lemonade STT** (OpenAI `/v1/audio/transcriptions`, Whisper-Large-v3-Turbo on whisper.cpp; `STT_BACKEND` ∈ cpu|gpu, or the experimental NPU tier via `docker-compose.override.npu.yml` + `STT_MODEL`; decode quality via the `STT_*` vars on the container — empty disables each, NPU/flm ignores them) → transcript dispatched as `channel/message` → reply spoken as it streams, segment-by-segment with prefetch, via **Lemonade Kokoro** (`/v1/audio/speech`, 24 kHz PCM resampled in-hub to 22 050 Hz) → back as `audio-start`/`audio-chunk`/`audio-stop`.

**Choosing the STT model.** `STT_MODEL` names it on both sides at once — the container pre-pulls it,
the hub requests it (`Stt__OpenAi__Model`) — and the two must agree or the warmed model is not the
one decoded with. A built-in Lemonade name is the whole request. A **fine-tune is a `user.` model**,
which exists in no registry until the entrypoint's pre-pull registers it: `/api/v1/pull` doubles as
the registration endpoint, and without `recipe` + `checkpoints` it answers a 400 demanding the very
`user.` namespace it was handed (the same misleading error the FLM tier hits). `STT_MODEL_CHECKPOINT`
carries that checkpoint as `org/repo:file.bin`; Lemonade resolves it against HuggingFace/ModelScope
only, so a locally converted model must be published before it can be named — there is no local-path
form. A `user.` model with no checkpoint fails the container at boot rather than starting one whose
every transcription 400s. **A fine-tune moves the confidence distribution the gibberish gate is cut
against**: `AvgLogProbThreshold` / `ShortSpeechAvgLogProbThreshold` / `NoSpeechProbThreshold` are
measured numbers, so re-measure them on the new model instead of carrying the old floors across.

**Alert routing.** Insistent announces — timers and alarms, i.e. exactly the `/api/voice/announce`
requests carrying `insistent` — are marked `alert: true` on the Wyoming `audio-start` (protocol
1.5, `SatelliteConnection.BuildAudioStart`; `InsistentAnnouncementController` is the only producer,
via `PlaybackKind.Alarm`, which is what the playback queue reads to mark the stream — a producer
cannot mark audio that is not an alert). The satellite plays a marked stream on
`--alert-snd-command` instead of
`--snd-command`: on music units a non-attenuated `alert` ALSA softvol, so an alert is not capped by
the calibrated conversational `TTS` level. `AnnouncePriority.High` is deliberately not the marker —
approval prompts share it. Every other kind is unmarked, so ordinary replies, plain
announcements and a pre-1.5 satellite are unaffected, and an unopenable alert device falls back to
the normal sink rather than dropping the connection. A music unit's level chain is three per-source
softvols (`Music`, `TTS`, `Alert`) under a PipeWire master that starts at `MASTER_VOLUME` %
(default 50, persisted by wireplumber once the user moves it); see
`scripts/provision-satellite-rs.sh` for `MASTER_VOLUME` / `TTS_VOLUME` / `ALERT_VOLUME`. A
voice-only unit has no PipeWire and no per-source calibration: it plays everything, alerts
included, through a single ALSA softvol master that the spoken volume commands drive, landed on
the same startup level each boot by a provisioning-installed oneshot.

**Local speaker commands.** `VoiceCommandMatcher` matches a normalized whole transcript (lowercase,
accents and punctuation stripped, whitespace collapsed) against `VoiceSettings.Commands.Phrases`.
`LocalCommandDispatcher` (`Services/LocalCommands/`) routes a match to the `ILocalCommandHandler`
that owns it — handlers are DI-registered and self-declare their commands; construction fails fast
on duplicate or unowned commands. `TranscriptDispatcher` calls it AFTER the gibberish gate and
BEFORE `GetOrCreateAsync`, so poor audio cannot move a volume knob and a hit costs no
`create_conversation` round trip. `SpeakerVolumeCommandHandler` writes `speaker-volume` through
`SatelliteSession.ControlWriter`; a hit returns `false`, which `FollowUpConversation` already turns
into `EndConversation`. A new local command is a new enum value plus a handler registration —
`TranscriptDispatcher` stays untouched. Every phrase carries an explicit local marker: "sube el
volumen" is a Music Assistant request and still belongs to the agent. Matching is whole-transcript
only, so a compound sentence goes to the agent intact.
**A local mute must never swallow a timer or an alarm.** `InsistentAnnouncementController` keeps
the speaker audible for the whole ring, in two parts. It re-sends `alert-hold` to every online
target at the top of EVERY round, not once before the loop: the satellite's hold dies with its TCP
connection, so a reconnect mid-alarm needs a fresh one, and a satellite that was rebooting when
the loop started gets one as soon as it comes back. The satellite's hold is idempotent, so a
re-assert to a satellite that already holds costs nothing.

The release is refcounted, because overlapping alerts on one satellite are normal (a timer and an
alarm on the same speaker). `ActiveAlertRegistry.CountFor` says how many alerts still cover a
satellite. The ring loop's `finally` discards its OWN handle first, then sends `alert-release`
only to targets whose count reached zero. Discarding before counting is what stops an alert from
counting itself; it also means an alert whose loop died before it ever sent a hold cannot release
somebody else's.

**Short-phrase decode.** Three hub-side pieces target the one-to-three-second command, where whisper
has the least context and every gate is hardest on it. (1) Every transcription POSTs a `prompt`:
`WhisperPromptBuilder` composes `Stt.OpenAi.Prompt` — `{room}`/`{locality}` placeholders, per-satellite
overridable via `Satellites__<id>__Stt__OpenAi__Prompt` — followed by the prior segment's transcript,
capped at `MaxPromptChars` by trimming the prior text from its front so the configured vocabulary
survives whole. A per-request prompt replaces whisper-server's own `--prompt`, so the container
default only serves non-hub callers. (2) `Stt.Streaming` will not split before `FirstSplitAfterMs`
(4 s), so a short command is always decoded whole, and `ChainContext` feeds each fragment the
previous one's text — which serializes decodes, making `MaxInFlightDecodes > 1` inert (warned at
construction). (3) The gibberish gate's `avg_logprob` floor loosens to
`ShortSpeechAvgLogProbThreshold` below `FullThresholdSpeechMs` of measured speech, because a short
turn scores lower than a long one for reasons unrelated to being wrong. Measured on prod: a 2.9 s
clip scored −0.12 and a 0.75 s clip −0.23, and splitting one utterance in two produced a wrong verb
and a duplicated number.

**End-of-utterance floor.** `SilenceGate` classifies speech against a noise floor that `AdaptiveLevelTracker` measures inside the capture and freezes at the first accepted speech frame — so a capture that opens on sound (a command running straight on from the wake word) has nothing but that sound to measure. Two measurements taken where the room is actually audible cap it, and can only ever lower it: the satellite's own `room_rms` on `run-pipeline` (protocol 1.7, idle audio), and `RoomNoiseMemory`, a per-satellite window of recent background samples the hub keeps from its own captures (a no-speech capture's floor, or the trailing run that ended a capture — `WyomingClient.RoomLevelSamples` / `RoomLevelRetentionSeconds`). Without either the gate behaves exactly as before. An inflated floor is not a cosmetic error: it arms the adaptive regime, whose `PeakDropDb` backstop then reads normal syllable dynamics as background and endpoints the user mid-sentence.

**One connection module, one host.** `SatelliteConnection` owns one run of the link to a satellite:
registering it with the session registry and the wake arbiter, launching the playback and
conversation tasks, routing the frames the satellite sends, and unwinding in order. `WyomingSatelliteHost`
keeps what is genuinely its own — discovering which satellites have an address, dialling them,
parsing addresses, reconnecting forever, and the turn helpers (transcription and dispatch, the early
speaker check, the chime, every metric publish). Its internal `CreateConnection` returns a fully
wired connection, which is how a unit test drives frame routing without a socket while still
exercising the real publishing code.

The wire arrives as two halves: the write side is a delegate supplied at construction (the
coordinator's end-of-turn write and the arbiter's re-arm handle both close over it before the read
loop starts), the read side an `IAsyncEnumerable<WyomingEvent>` passed to the run. The host keeps the
`WyomingClient` and disposes it when the run returns, and the run still throws when the link drops so
the reconnect loop catches and retries. **The unwind is split on purpose**: a synchronous phase
releases the arbiter registration, then an asynchronous phase drains the rest. A satellite whose link
just died must stop being an arbitration candidate before anything unbounded runs, or it can still
win a wake against a live satellite and silently suppress a real command — and a method that cannot
await cannot be reordered behind one.

**One gate factory, one microphone, one turn module, one playback queue.** `SilenceGateFactory` (singleton) is the only thing that knows how a satellite's gate is put together: it resolves the per-satellite `Gate` overrides against `WyomingClientSettings`, applies the room cap, and owns the `RoomNoiseMemory` per satellite — keyed that way deliberately, so it outlives a connection. There is no gate-purpose parameter: the wake capture and the approval capture ask for the same gate, which is what stopped them drifting apart (the approval mic used to run uncapped and cut people off mid-answer). Every live capture also pays back into the memory as it closes, and nothing has to remember to: `Microphone.Close` records the gate statistics as one act with closing, so the wake capture, the follow-up capture and the approval mic all pay through the same line. A capture that only reads would let the memory expire on a satellite used mostly for approvals. Never assemble an `AdaptiveLevelTracker` at a call site.

`VoiceTurn` (`session.Turn`) owns the whole per-turn reply handshake — the started/outstanding/stream-complete/audio-played counters, the epoch, the preamble claim and the dispatch stamp — and offers no route to the settle rule from outside. `BeginSegment()` returns a `SegmentToken` carrying its own epoch, so a segment cannot be released against a turn it never registered on. The reply tool ends a turn with one `EndStream()` and never reads a counter. `Microphone` (`session.Mic`) owns the microphone itself and knows nothing about turns: open, close, feed, force-end, abort, and what the open capture is hearing — plus the pairing of closing with paying back into the room-noise memory. The session has no capture surface of its own. `CaptureSession` owns the **turn** semantics on top of it: opening a **wake turn** (which takes the **wake announcement** the satellite reported and records its room level immediately before building the gate that reads it back), opening a **follow-up turn** (which takes nothing — a follow-up has no wake of its own, so it can never report a loudness it did not measure), close (frozen gate stats, room sample and speech-end anchor at that instant), and the two indicator events, which go through `SatelliteSession.TrySendControlAsync` because they are indicator-only and must never cost the user the utterance. `FollowUpConversation` takes those two modules plus seven delegates and one int: three required — `TranscribeAndDispatch`, `EnqueueChime`, `EndConversation` — and four optional with no-op defaults — `OnFollowUpWindow`, `OnSilenceTimeout`, `OnReplyTimeout`, `EarlyReject`, the last paired with `EarlyVerifyMs`. Which of the two types a listening path holds is the statement of what it is doing: `RequestApprovalTool` holds the microphone directly, because an approval prompt is a question the agent asked mid-turn and must not mark a turn start or a speech end — doing so would report its latency against the turn actually in flight (`docs/adr/0013-the-microphone-and-the-turn-are-separate-types.md`).

`PlaybackQueue` (`session.Playback`) owns everything queued to be heard on one satellite, and promises **exactly one outcome per job** — heard to the end, cut short, broken, refused, or discarded because the connection died — including a job it turned away and every job still waiting when the link drops (`docs/adr/0003-playback-settles-by-outcome.md`). Queueing hands back a `PlaybackTicket`: a `RefusalReason?` answered immediately and the one `Task<PlaybackOutcome>` that will end it, so a producer that must wait awaits one thing and a producer with nothing to wait for reads the refusal. The queue reads a job's `PlaybackKind` to pick its depth allowance, whether its synthesis starts early, and whether it takes the alert route; it owns that synthesis and disposes it on every outcome, so no producer disposes anything. It signals an outcome and advances — a producer's reaction runs on its own and carries its own `try`/`catch`, and only the connection's own hooks (the frame writer, the audio envelope, the per-job error metric) stay awaited inside the loop. The host builds it as it assembles a connection; `SatelliteConnection` runs its loop and settles its unplayed jobs from the drain.

Sending a `transcript` event ends the satellite's turn and re-arms wake; `FollowUpConversation` reopens the mic wake-free, announced by the `ListeningChime` earcon and, on the wire, by a `listening-started` event (protocol 1.6) that returns the satellite's LED from Thinking to Listening — it cannot infer the moment itself, because its capture never closed. When several satellites hear the same wake word, `WakeArbiter` picks one winner (calibrated `wake_rms`, 500 ms coincidence window, onset-alignment check against open captures) and silently re-arms the losers via `pause-satellite`; a much-louder wake during another satellite's open conversation hands the conversation over.

**The satellite side is `satellite/CLAUDE.md` — read it before touching either side of the wire.** What the hub must respect: the satellite is the Wyoming **server** (the hub dials in), and its playback sink is FIXED 22 050 Hz mono S16LE regardless of announced rates, so all hub-emitted audio (TTS, `ListeningChime`) must be 22 050 Hz. The dockerized hub dials the dev satellite addresses only under `ASPNETCORE_ENVIRONMENT=Development` (`McpChannelVoice/appsettings.Development.json` overrides exactly the `Satellites` addresses; production points at the Pi IPs).
