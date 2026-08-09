# nabu-satellite

Standalone Rust crate (NOT in the .NET solution): a fully static aarch64-musl Wyoming satellite binary (~19.9 MiB) for Raspberry Pi, embedding the openWakeWord "ok nabu" pipeline (melspectrogram → embedding → classifier ONNX, run in-process via tract) and the cue WAVs. `README.md` covers build prerequisites, hardware defaults, the LED and model licenses; this file holds the invariants that must not be broken. Subsystem deep dives are path-scoped rules: `.claude/rules/satellite-volume.md` (music ducking, local speaker volume) and `.claude/rules/satellite-hardware-deploy.md` (the deployed XVF3800/MiniAmp unit, build, provisioning, WSL runs).

## Invariants

- **The satellite is the Wyoming SERVER; the hub dials in** (default `--listen 0.0.0.0:10700`). A new hub connection supersedes the previous one (abort + await), so a dead-peer TCP wedge can't hold the exclusive `plughw` mic for the ~15-min retransmission timeout. The three ONNX models are parsed + optimized ONCE at boot (`WakeModels::load`, fail-fast) and shared across connections, so re-arm after a reconnect is instant.
- **Cancellation safety**: hub/mic reads AND playback writes/drains are multi-await compound I/O, NOT `select!`-safe. They run in dedicated pump tasks (hub, mic, playback) feeding bounded mpsc channels; the main `select!` only races `recv()` futures.
- **Playback pump** — the single owner of the playback device. `audio-stop`'s drain (~0.5-2 s of buffered TTS) happens inside the pump, so wake/button/mic stay live during the reply tail. Drain completions return on an unbounded channel (bounded would AB-deadlock) carrying a generation that gates the LED Idle/Listening transition, so a stale completion can't blank a newer stream. Playback errors stay connection-fatal, the alert sink's open being the one deliberate exception (below). Cues route through the pump too (and are dropped while a stream is active), so a cue can never EBUSY-race a reply for the exclusive device.
- **Audio contract**: mic = 16 kHz mono S16LE in 1280-sample/80 ms chunks (arecord subprocess; bytes end-to-end internally, decoded to i16 only at the detector). Playback sink = FIXED 22 050 Hz mono S16LE (aplay) that ignores announced rates — hub-side TTS, the chime and the embedded cue WAVs must all be 22 050 Hz.
- **ALSA latency flags**: the default audio commands carry latency-tuned flags (`README.md` explains them). Keep them when overriding devices — on **both** `--snd-command` and `--alert-snd-command`. Plain-argv audio commands exec directly (no `sh -c`) so kill/supersede SIGKILLs aplay/arecord themselves; shell-shaped commands (WSL gain pipe) still go through sh.
- **Zero-lag pre-roll**: while idle, mic chunks fill a pre-roll ring (`--preroll-ms`, default 1000); a wake trigger flushes only the detection gap (3 chunks ≈ 240 ms), never the wake word itself; a button press flushes the full ring.
- **Wire format**: frames are one contiguous buffer with event `data` sent once as the `data_length` body (the hub's reader prefers the body; its writer emits the same shape) — pinned by a codec test.

## Wake Metadata & Arbitration (PROTOCOL_VERSION 1.7)

`run-pipeline` carries `{"source":"wake"|"button","wake_rms":f32,"wake_score":f32}` — rms over the pre-roll ring minus the detection gap, BEFORE trim, in i16-amplitude units matching the hub's SilenceGate. The hub may reply `pause-satellite` (arbitration loss): Streaming → audio-stop back, Idle, detector reset, NO cue, LED Idle; Idle → no-op.

**`room_rms` (1.7, optional)** — what the room sounds like with nobody talking to the satellite, from `audio/room.rs`: the minimum of 480 ms-smoothed energy over a trailing 3 s of **idle** mic audio, same units and same statistic as the hub's own noise floor. It exists because the hub cannot measure this: its first captured frame is already the turn, so a command spoken straight after the wake word leaves its `AdaptiveLevelTracker` estimating the background from the speaker's own voice (measured at 6x the true room on prod, which armed the noisy-room regime in a silent office and endpointed people mid-sentence). Smoothing before the minimum is what stops bursty TV dialog from reading as a quiet room through its 100-400 ms lulls; the wake word needs no exclusion despite sitting at the end of the window, because loud audio cannot lower a minimum. The estimator is **reset at every trigger**, so a reading only ever describes idle audio measured since the last turn ended — a satellite that has not been idle for a full window sends **no field at all** (absent, never null or zero: the hub reads a missing value as "ask my own captures", and a zero would pin its floor at silence).

The measurement is taken **before the duck engages** (ducking starts at Listening, i.e. at the trigger), which is what makes it safe on a music unit: with music playing it reports the *unducked* level, louder than the ducked music the capture then hears, so the hub's cap is inert and its adaptive gating stays armed exactly where it is needed. In a quiet room the two are the same number.

A button-triggered `run-pipeline` carries only `{"source":"button"}` with no `wake_rms`, and the hub marks a connection pause-capable only after seeing a non-null `wake_rms` — so a satellite whose first turn on a connection is a button press still gets the legacy audible `transcript` abort if that turn loses arbitration.

The hub also sends `voice-stopped` (header-only) once it has endpointed the user's speech and is about to transcribe — deliberately NOT sent for captures abandoned to arbitration or ending in silence. The satellite uses it purely as the Thinking indicator and does **not** close its capture on it; only `transcript`, the actual turn-end signal, does that.

`listening-started` (header-only, 1.6) is its counterpart: the hub sends it from
`FollowUpConversation` just before it reopens the mic for a wake-free follow-up turn, and the
satellite uses it purely to move the turn's LED phase back to Listening. The satellite cannot
infer that moment — its own capture never closed, so from its side a reply draining looks
identical whether the agent is mid-answer or finished. Ignored while Idle, and a pre-1.6 hub
simply never sends it (the ring then keeps breathing through the window).

`audio-start` carries `alert: bool` (1.5). `true` routes the stream to `--alert-snd-command`
instead of `--snd-command` — on music units a non-attenuated `alert` softvol, so a timer or alarm
rings at full scale rather than the calibrated conversational `TTS` level. The hub sets it only for
insistent announces (timers and alarms). Read defensively and defaulted to `false`, so a pre-1.5
hub, an ordinary reply and a garbage value all keep the normal sink. If the alert device cannot be
opened the pump falls back to the normal sink and warns — never connection-fatal on open, because a
quiet alarm beats a dropped connection (a player that outlives the probe and dies mid-ring is still
an ordinary fatal playback error). That covers **both** failure shapes, which is why the fallback is
not merely an `Err` branch: a missing player binary fails `spawn()`, but the realistic case — an
undefined `pcm.alert` — spawns fine and dies on the device open, invisible until writes EPIPE well
into the ring. So an alert sink gets a 50 ms `try_wait` liveness probe before a stream is committed
to it. **Don't "optimize away" that sleep**: it is on the alert path only, never replies or cues, and
without it a misconfigured alert device drops the hub connection for the whole duration of the alarm
(the insistent loop re-enqueues on every gap).

## LED

The state machine publishes `LedState` (Idle/Listening/Thinking/Speaking) on a tokio watch channel; a per-connection render task owns the backend — the reSpeaker XVF3800's 12-LED ring over USB vendor control transfers. The protocol constants, the per-phase looks and the colour-before-effect rule live in `led.rs`; the phase→look table is in `README.md`.

- **Phase mapping** (`handle_hub_event`): `run-pipeline` (wake or button) → Listening; `voice-stopped` → Thinking (capture stays open); `listening-started` → Listening; `audio-start` → Speaking; `transcript` → Idle. Idle after a reply therefore means actual-playback-complete. A 120 s Thinking fallback mirrors the hub reply timeout in case `voice-stopped` fired but no reply/transcript ever arrives.
- **A stream draining mid-turn returns the ring to the turn's phase, not to a fixed state.** `run_connection` carries `phase` (Listening/Thinking) alongside `mode`, and `apply_drain_done` publishes it whenever `mode` is still `Streaming` — Idle otherwise. This is what makes the seams of a segmented answer (say something → call a tool → say the rest) read as Thinking: they are one hub turn, so no `transcript` arrives between them, and hard-coding Listening there put the ring on the DoA look, which with no sound in the room renders as good as dark. Listening is still correct **before** `voice-stopped` — an announcement draining while a fresh capture is open — which is the case that mapping was originally added for.
- Enabled by default when 2886:001a is on USB (`--no-led` opts out); an absent device or USB subsystem is silent, a failed open warns. The service needs write access to the USB node (udev rule + `plugdev` group, see `README.md`); without it the ring stays dark. `main.rs` blanks the ring at startup and on SIGTERM/SIGINT, because the ring's power-on default is lit and the render task only exists while a hub is connected.
