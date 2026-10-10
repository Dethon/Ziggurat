use crate::audio::capture::{bytes_to_samples, MicCapture};
use crate::audio::cues::Cues;
use crate::audio::playback::{spawn_pump, DrainDone, PlaybackHandle};
use crate::audio::room::RoomLevel;
use crate::config::Config;
use crate::gpio;
use crate::led::{self, LedState};
use crate::wake::{WakeDetector, WakeModels};
use crate::wyoming::codec::{read_event_buffered, write_event};
use crate::wyoming::WyomingEvent;
use serde_json::json;
use std::collections::VecDeque;
use tokio::io::{AsyncWrite, BufReader};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::sync::{mpsc, watch};
use tracing::{info, warn};

/// Room-measurement windows. The smoothing span matches the hub's floor smoothing (bursty TV
/// dialog must not read as a quiet room through its 100-400 ms lulls) and the trailing window
/// matches its FloorWindowMs, so both ends of the wire describe the background the same way.
const ROOM_SMOOTHING_MS: usize = 480;
const ROOM_WINDOW_MS: usize = 3000;

#[derive(PartialEq, Clone, Copy, Debug)]
enum Mode { Idle, Streaming }

/// Aborts the wrapped task on drop, so the pump tasks can never outlive run_connection —
/// neither on loop exit / `?` error paths, nor when the whole connection task is aborted
/// (main.rs single-hub supersede policy; the mic pump owns MicCapture, whose kill_on_drop
/// must reap arecord before the next connection can claim the exclusive device).
struct AbortOnDrop(tokio::task::JoinHandle<()>);
impl Drop for AbortOnDrop {
    fn drop(&mut self) { self.0.abort(); }
}

/// Immutable per-connection context threaded through the event handlers (bundled to keep
/// the signatures within clippy's argument limit).
struct Ctx<'a> {
    cues: &'a Cues,
    led: &'a watch::Sender<LedState>,
    /// Process-scoped, unlike the rest of this struct: the alert hold and the user's mute are read
    /// together on every decision, and both have to survive a hub reconnect mid-alarm.
    volume: &'a std::sync::Arc<crate::volume::VolumeControl>,
    /// Per-connection, same as the fields above: one handle owns the device for the whole
    /// connection, shared (never exclusively borrowed) because every method here — including
    /// `start`, since PlaybackHandle's generation counter is atomic — only ever needs `&self`.
    playback: &'a PlaybackHandle,
}

/// Ends an outstanding alert hold if the connection dies mid-alarm, putting the sink back to what
/// the user asked for — audible if they never muted, muted if their mute is still deferred by the
/// hold. Drop cannot await, so `release_hold_detached` fires a detached wpctl — the same fail-safe
/// shape as music.rs's DuckGuard. The hold now lives in the process-scoped VolumeControl, so this
/// guard carries nothing else.
struct HoldGuard {
    volume: std::sync::Arc<crate::volume::VolumeControl>,
}

impl Drop for HoldGuard {
    fn drop(&mut self) {
        self.volume.release_hold_detached();
    }
}

pub async fn run_connection(
    reader: OwnedReadHalf, writer: OwnedWriteHalf, cfg: Config, models: Option<WakeModels>,
    volume: std::sync::Arc<crate::volume::VolumeControl>,
) -> anyhow::Result<()> {
    let mut wr = writer;
    let mic = MicCapture::spawn(&cfg.mic_command)?;
    let mut detector =
        models.as_ref().map(|m| WakeDetector::new(m, cfg.detector.clone())).transpose()?;
    let cues = Cues::new(&cfg)?;

    // CANCELLATION SAFETY: tokio::select! DROPS the futures of losing arms. Both
    // read_event_buffered and MicCapture::next_chunk are multi-await compound reads, so
    // dropping them mid-read loses partial progress: a hub event spanning TCP segments is
    // half-consumed and the next read parses PCM payload as a header line ("stream did not
    // contain valid UTF-8"); a half-read mic chunk drops bytes and misaligns the i16 stream.
    // The compound reads therefore live in dedicated pump tasks, and the select! below races
    // only mpsc::Receiver::recv() futures, which ARE cancellation-safe. Bounded channels
    // preserve flow control: when the main loop blocks, the pumps block on send() and the
    // socket / arecord pipe back up exactly as before. Playback writes/drains are compound
    // I/O too — they live in the playback pump (spawned below), not in this loop.
    let (hub_tx, mut hub_rx) = mpsc::channel::<anyhow::Result<WyomingEvent>>(16);
    let _hub_pump = AbortOnDrop(tokio::spawn(async move {
        // 32 KiB (vs the 8 KiB default): TTS receive bursts at 100+ frames/s; a bigger read
        // buffer quarters the read syscalls while the loop competes with playback for CPU.
        let mut buf = BufReader::with_capacity(32 * 1024, reader);
        loop {
            match read_event_buffered(&mut buf).await {
                Ok(Some(e)) => {
                    if hub_tx.send(Ok(e)).await.is_err() { break; } // main loop gone
                }
                Ok(None) => break, // clean EOF -> drop tx -> recv() yields None
                Err(e) => { let _ = hub_tx.send(Err(e)).await; break; }
            }
        }
    }));
    let (mic_tx, mut mic_rx) = mpsc::channel::<anyhow::Result<Vec<u8>>>(8);
    let _mic_pump = AbortOnDrop(tokio::spawn(async move {
        let mut mic = mic;
        loop {
            match mic.next_chunk().await {
                Ok(Some(samples)) => {
                    if mic_tx.send(Ok(samples)).await.is_err() { break; } // main loop gone
                }
                Ok(None) => break, // EOF -> drop tx -> recv() yields None
                Err(e) => { let _ = mic_tx.send(Err(e)).await; break; }
            }
        }
        // MicCapture drops here -> kill_on_drop reaps the arecord child
    }));

    // Button is claimed per-connection, released on disconnect (ButtonGuard drop). An "empty"
    // receiver (sender already dropped) leaves the select! branch permanently disabled.
    let (_button_guard, mut button_rx) = match gpio::spawn_button(&cfg.button) {
        Ok(Some((g, rx))) => (Some(g), rx),
        Ok(None) => (None, mpsc::channel(1).1),
        Err(e) => { warn!("button unavailable: {e:#}"); (None, mpsc::channel(1).1) }
    };

    // LED is claimed per-connection like the button; guard drop (connection end/supersede)
    // aborts the render task, whose backend turns the light off on drop.
    let (led_tx, led_rx) = watch::channel(LedState::Idle);
    let duck_rx = led_tx.subscribe();
    let _led_guard = led::spawn_led(&cfg.led, led_rx);
    let _duck_guard = crate::music::spawn_duck(
        duck_rx,
        cfg.music_mixer.clone(),
        cfg.music_card.clone(),
        cfg.duck_percent,
        std::time::Duration::from_millis(cfg.music_restore_grace_ms),
    );
    // Playback is a pump task too: PlaybackSink::finish() waits for the player to drain
    // (≈0.5-2 s of buffered TTS after every reply) and must not park this loop — wake/button
    // re-arm and mic forwarding stay live during the drain. Completions come back on an
    // unbounded channel (a bounded send from the pump could AB-deadlock against a main loop
    // blocked sending a command) and are raced below like the other pumps.
    let (playback, mut playback_done, pump_task) =
        spawn_pump(&cfg.snd_command, &cfg.alert_snd_command);
    let _playback_pump = AbortOnDrop(pump_task);
    let ctx = Ctx { cues: &cues, led: &led_tx, volume: &volume, playback: &playback };
    // Covers every exit path — the ? error paths below and the task abort on connection
    // supersede alike — because both simply drop this local.
    let _hold_guard = HoldGuard { volume: volume.clone() };

    // Pre-roll ring: keep the last `preroll_chunks()` mic chunks while Idle, so a request spoken
    // immediately after the wake word/button is never clipped (the zero-lag requirement).
    let preroll_cap = cfg.preroll_chunks();
    let mut preroll: VecDeque<Vec<u8>> = VecDeque::with_capacity(preroll_cap + 1);

    // Measured off the same idle mic audio the pre-roll ring holds, and reported to the hub on
    // every trigger (see start_turn): the hub's gate has no other way to know what silence sounds
    // like in this room before the user starts talking into it.
    let mut room = RoomLevel::new(ROOM_SMOOTHING_MS, ROOM_WINDOW_MS);

    let mut mode = Mode::Idle;
    // The LED phase the open turn sits in between playback streams: Listening while the mic is
    // genuinely capturing the user, Thinking once the hub has endpointed the utterance and the
    // agent is working. A stream draining mid-turn returns the ring here, which is what makes the
    // gaps in a segmented reply (answer, tool call, answer) read as "still working". Only ever
    // read while mode is Streaming, and start_turn re-seeds it on every turn, so it cannot go stale.
    let mut phase = LedState::Listening;

    loop {
        tokio::select! {
            ev = hub_rx.recv() => match ev {
                None => { info!("hub disconnected"); break; }
                Some(Err(e)) => return Err(e),
                Some(Ok(e)) => handle_hub_event(e, &mut mode, &mut phase, detector.as_mut(), &mut wr, &ctx).await?,
            },
            done = playback_done.recv() => match done {
                None => anyhow::bail!("playback pump terminated"),
                Some(d) => apply_drain_done(d, playback.latest_generation(), mode, phase, ctx.led)?,
            },
            chunk = mic_rx.recv() => match chunk {
                None => { warn!("mic stream ended"); break; }
                Some(Err(e)) => return Err(e),
                Some(Ok(bytes)) => match mode {
                    Mode::Idle => {
                        // decode BEFORE the ring takes ownership — no clone. Needed by the wake
                        // detector and by the room measurement, so it happens even with --no-wake.
                        let samples = bytes_to_samples(&bytes);
                        room.push(&samples);
                        let samples = detector.is_some().then_some(samples);
                        push_preroll(&mut preroll, bytes, preroll_cap);
                        if let (Some(d), Some(samples)) = (detector.as_mut(), samples) {
                            let t0 = std::time::Instant::now();
                            let fired = d.push_chunk(&samples);
                            // on-device budget check: must stay well under the 80 ms chunk cadence
                            tracing::debug!(us = t0.elapsed().as_micros() as u64, "wake inference");
                            if let Some(score) = fired {
                                // ring_rms BEFORE trim_preroll: the trim drops exactly the
                                // wake-word audio this measures.
                                let wake = WakeSignal { rms: ring_rms(&preroll, cfg.wake_preroll_chunks()), score };
                                if wake.audible(cfg.wake_min_rms) {
                                    info!("wake word detected");
                                    trim_preroll(&mut preroll, cfg.wake_preroll_chunks());
                                    let measured = room.rms();
                                    room.reset();
                                    start_turn(&mut wr, &mut mode, &mut phase, &ctx, &mut preroll,
                                        Some(wake), measured).await?;
                                } else {
                                    // Not a turn, so nothing a turn owns is touched: the ring
                                    // and the room estimate keep describing idle audio. The
                                    // detector armed its own refractory when it fired, which is
                                    // what stops the same sound firing again on the next frame.
                                    info!(score, rms = wake.rms, floor = cfg.wake_min_rms,
                                        "wake rejected: quieter than --wake-min-rms");
                                }
                            }
                        }
                    }
                    Mode::Streaming => {
                        write_event(&mut wr, &WyomingEvent::audio_chunk(16000, 2, 1, bytes)).await?;
                    }
                },
            },
            Some(()) = button_rx.recv() => {
                if mode == Mode::Idle {
                    info!("button pressed -> start turn");
                    if let Some(d) = detector.as_mut() { d.reset(); }
                    let measured = room.rms();
                    room.reset();
                    start_turn(&mut wr, &mut mode, &mut phase, &ctx, &mut preroll, None, measured).await?;
                }
            }
        }
    }
    // _hub_pump/_mic_pump/_playback_pump drop here (as on every early-return path) and abort
    // their tasks.
    Ok(())
}

/// A reply/announcement finished draining out of the player. Mid-turn the ring returns to the
/// turn's own phase — Thinking between the segments of one answer, Listening while the mic is
/// actually capturing — because a stream ending says nothing about what the turn is doing next;
/// with no turn open it goes dark. The transition is generation-gated: a stale completion
/// arriving after a newer audio-start must not blank the LED mid-Speaking. Playback failures stay
/// connection-fatal (the hub redials and a fresh connection re-arms everything; best-effort-continue
/// would hide a dead audio device).
fn apply_drain_done(
    d: DrainDone, latest_generation: u64, mode: Mode, phase: LedState,
    led: &watch::Sender<LedState>,
) -> anyhow::Result<()> {
    d.result?;
    tracing::debug!(gen = d.generation, latest = latest_generation, ?mode, ?phase, "drain done");
    if d.generation == latest_generation {
        let _ = led.send(if mode == Mode::Streaming { phase } else { LedState::Idle });
    }
    Ok(())
}

fn push_preroll(buf: &mut VecDeque<Vec<u8>>, chunk: Vec<u8>, cap: usize) {
    buf.push_back(chunk);
    while buf.len() > cap { buf.pop_front(); }
}

/// Wake-path trim: keep only the newest `keep` chunks (the detection-latency gap),
/// dropping the wake-word audio that precedes them. Button turns skip this — speech
/// may legitimately precede a button press, so they flush the full ring.
fn trim_preroll(buf: &mut VecDeque<Vec<u8>>, keep: usize) {
    while buf.len() > keep { buf.pop_front(); }
}

/// Loudness of the wake word as this satellite's mic heard it, for hub-side arbitration:
/// combined RMS (i16-amplitude units, hub-comparable) over the pre-roll ring EXCLUDING the
/// newest `exclude_newest` chunks — those are the detection-latency gap after the word ends.
/// Must run before trim_preroll, which drops exactly the audio this measures.
fn ring_rms(buf: &VecDeque<Vec<u8>>, exclude_newest: usize) -> f32 {
    let take = buf.len().saturating_sub(exclude_newest);
    let (sum_sq, n) = buf.iter().take(take).fold((0f64, 0usize), |(s, n), bytes| {
        let samples = bytes_to_samples(bytes);
        (s + samples.iter().map(|&v| v as f64 * v as f64).sum::<f64>(), n + samples.len())
    });
    if n == 0 { 0.0 } else { (sum_sq / n as f64).sqrt() as f32 }
}

/// Wake-trigger metadata reported to the hub on `run-pipeline` so it can arbitrate between
/// multiple satellites that heard the same wake word (louder/higher-confidence mic wins).
struct WakeSignal {
    rms: f32,
    score: f32,
}

impl WakeSignal {
    /// Whether the audio the classifier fired on was loud enough to have been somebody speaking.
    /// The score cannot answer this: fires out of a near-silent room carry scores up to 0.98.
    fn audible(&self, min_rms: f32) -> bool { self.rms >= min_rms }
}

/// End an active capture: send audio-stop to the hub, transition to Idle, reset the wake detector.
/// Both transcript (normal end-of-turn) and pause-satellite (arbitration loss) use this common path.
async fn end_capture<W: AsyncWrite + Unpin>(
    wr: &mut W,
    mode: &mut Mode,
    detector: Option<&mut WakeDetector>,
) -> anyhow::Result<()> {
    write_event(wr, &WyomingEvent::with_data("audio-stop", json!({"timestamp":0}))).await?;
    *mode = Mode::Idle;
    if let Some(d) = detector { d.reset(); }
    Ok(())
}

/// On trigger: announce the pipeline, play the awake cue, then FLUSH the pre-roll to the hub
/// before going live. This is the zero-lag guarantee — buffered audio reaches the hub regardless
/// of how fast the user starts speaking or how long the hub takes to open its capture.
/// `wake` is `Some` for a wake-word trigger (carrying the mic-side RMS/score for hub
/// arbitration) or `None` for a button trigger (no wake signal to report).
async fn start_turn<W: AsyncWrite + Unpin>(
    wr: &mut W, mode: &mut Mode, phase: &mut LedState, ctx: &Ctx<'_>,
    preroll: &mut VecDeque<Vec<u8>>, wake: Option<WakeSignal>,
    room: Option<f32>,
) -> anyhow::Result<()> {
    let mut data = match &wake {
        Some(w) => json!({ "source": "wake", "wake_rms": w.rms, "wake_score": w.score }),
        None => json!({ "source": "button" }),
    };
    // Protocol 1.7: what the room sounded like while this satellite was idle. The hub's
    // end-of-utterance gate cannot measure that itself — its first frame is already the turn —
    // and without it a command spoken straight after the wake word leaves the gate estimating
    // its noise floor from the speaker's own voice. Omitted, not nulled, when the satellite has
    // not been idle long enough to stand behind a reading: the hub then falls back to what its
    // own captures have learned.
    if let (Some(rms), Some(obj)) = (room, data.as_object_mut()) {
        obj.insert("room_rms".into(), json!(rms));
    }
    write_event(wr, &WyomingEvent::with_data("run-pipeline", data)).await?;
    if let Some(pcm) = ctx.cues.awake() { ctx.playback.cue(pcm); }
    *phase = LedState::Listening;
    let _ = ctx.led.send(*phase);
    for chunk in preroll.drain(..) {
        write_event(wr, &WyomingEvent::audio_chunk(16000, 2, 1, chunk)).await?;
    }
    *mode = Mode::Streaming;
    Ok(())
}

async fn handle_hub_event<W: AsyncWrite + Unpin>(
    e: WyomingEvent,
    mode: &mut Mode,
    phase: &mut LedState,
    detector: Option<&mut WakeDetector>,
    wr: &mut W,
    ctx: &Ctx<'_>,
) -> anyhow::Result<()> {
    // Skip the per-frame audio-chunk flood (100+/s during a reply); the control events
    // (audio-start/stop, transcript, run-satellite) with the current mode are the useful trace.
    if e.event_type != "audio-chunk" {
        tracing::debug!(event = %e.event_type, ?mode, "hub event");
    }
    match e.event_type.as_str() {
        "run-satellite" => info!("run-satellite: armed"),
        "transcript" => {
            if *mode == Mode::Streaming {
                end_capture(wr, mode, detector).await?;
                if let Some(pcm) = ctx.cues.done() { ctx.playback.cue(pcm); }
                // Turn over (this event IS the hub's EndConversation, text always empty) —
                // the ring goes dark. Thinking is driven by voice-stopped, not by this.
                let _ = ctx.led.send(LedState::Idle);
            }
        }
        // The hub endpointed the user's speech and is now processing it. Capture stays open —
        // only the indicator changes — because the hub, not the satellite, closes the stream.
        // This also moves the turn's phase, so the gaps between the segments of a streamed
        // answer (say something, call a tool, say the rest) come back to Thinking rather than
        // to the mic-live look.
        "voice-stopped" => {
            if *mode == Mode::Streaming {
                *phase = LedState::Thinking;
                let _ = ctx.led.send(*phase);
            }
        }
        // The hub reopened the mic for a wake-free follow-up turn (protocol 1.6). The satellite's
        // capture never closed, so nothing changes but the phase — the turn is waiting on the user
        // again, not on the agent. A pre-1.6 hub never sends it and the ring simply keeps breathing
        // through the window.
        "listening-started" => {
            if *mode == Mode::Streaming {
                *phase = LedState::Listening;
                let _ = ctx.led.send(*phase);
            }
        }
        // Arbitration loss: another satellite won this utterance. End the capture like
        // transcript does, but silently — no done cue and straight to Idle, because from the
        // user's perspective this satellite was never part of the conversation.
        "pause-satellite" => {
            if *mode == Mode::Streaming {
                end_capture(wr, mode, detector).await?;
                let _ = ctx.led.send(LedState::Idle);
            }
        }
        // Playback errors surface as fatal through the pump's DrainDone/closed-channel paths
        // (see apply_drain_done). The pump owns the player; commands here never block on the
        // device, only on the bounded command channel (flow control).
        "audio-start" => {
            // A hub-marked alert (timer/alarm) plays on the non-attenuated alert route, bypassing
            // the per-satellite voice level. Read defensively: the field is peer-supplied and a
            // pre-1.5 hub omits it entirely, and this runs on the connection's event path where a
            // panic would drop the satellite mid-turn.
            let alert = e.data_obj().get("alert").and_then(|v| v.as_bool()).unwrap_or(false);
            ctx.playback.start(alert).await?;
            let _ = ctx.led.send(LedState::Speaking); // replies AND standalone announcements
        }
        "audio-chunk" => ctx.playback.pcm(e.payload).await?,
        "audio-stop" => {
            // The drain happens in the pump; the Idle/Listening LED transition fires when the
            // pump reports DrainDone (apply_drain_done), i.e. at actual playback end.
            ctx.playback.stop().await?;
        }
        // Local speaker volume (protocol 1.8). The hub sends intent, never numbers — step size
        // lives on the satellite, next to the hardware it applies to. Read defensively: this is
        // peer-supplied and runs on the connection's event path, where a panic drops the satellite.
        "speaker-volume" => {
            let action = e.data_obj().get("action").and_then(|v| v.as_str()).unwrap_or("").to_string();
            match action.as_str() {
                "up" | "down" => match ctx.volume.step(action == "up").await {
                    Ok(()) => { if let Some(pcm) = ctx.cues.volume() { ctx.playback.cue(pcm); } }
                    Err(e) => warn!("local volume step failed: {e:#}"),
                },
                "unmute" => match ctx.volume.set_user_mute(false).await {
                    Ok(()) => { if let Some(pcm) = ctx.cues.volume() { ctx.playback.cue(pcm); } }
                    Err(e) => warn!("local unmute failed: {e:#}"),
                },
                // Cue FIRST, mute after it has drained: muting the sink would otherwise silence
                // the very sound confirming the mute. The wait runs in a detached task so a
                // ~300 ms cue cannot stall mic forwarding on the select! loop.
                "mute" => {
                    let volume = ctx.volume.clone();
                    match ctx.cues.volume() {
                        Some(pcm) => {
                            let (tx, rx) = tokio::sync::oneshot::channel();
                            ctx.playback.cue_then(pcm, tx);
                            tokio::spawn(async move {
                                let _ = rx.await; // Err = cue dropped -> mute at once
                                if let Err(e) = volume.set_user_mute(true).await {
                                    warn!("local mute failed: {e:#}");
                                }
                            });
                        }
                        None => {
                            if let Err(e) = volume.set_user_mute(true).await {
                                warn!("local mute failed: {e:#}");
                            }
                        }
                    }
                }
                // An alarm must ring even on a muted speaker. The sink is unmuted WITHOUT clearing
                // the user's intent, so the release has something to restore. Both arms are thin
                // on purpose: the hold state lives in VolumeControl, next to the mute it decides
                // with. The hub re-sends the hold every round, and the hold is idempotent.
                "alert-hold" => {
                    if let Err(e) = ctx.volume.alert_hold().await {
                        warn!("alert unmute failed: {e:#}");
                    }
                }
                "alert-release" => {
                    if let Err(e) = ctx.volume.alert_release().await {
                        warn!("alert mute restore failed: {e:#}");
                    }
                }
                other => warn!("ignoring speaker-volume action {other}"),
            }
        }
        other => warn!("ignoring event {other}"),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::led::LedState;
    use crate::wyoming::codec::read_event;
    use serde_json::json;
    use std::sync::Arc;
    use tokio::sync::watch;

    fn cues() -> Cues {
        Cues::new(&Config::default()).unwrap()
    }

    fn pump() -> (PlaybackHandle, tokio::sync::mpsc::UnboundedReceiver<DrainDone>, AbortOnDrop) {
        let (handle, done_rx, task) = spawn_pump("cat >/dev/null", "cat >/dev/null");
        (handle, done_rx, AbortOnDrop(task))
    }

    // Like pump(), but with distinguishable sinks so a test can prove which one a stream opened.
    fn pump_with(
        normal: &std::path::Path,
        alert: &std::path::Path,
    ) -> (PlaybackHandle, tokio::sync::mpsc::UnboundedReceiver<DrainDone>, AbortOnDrop) {
        let (handle, done_rx, task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );
        (handle, done_rx, AbortOnDrop(task))
    }

    fn frame_paths(tag: &str) -> (std::path::PathBuf, std::path::PathBuf) {
        let dir = std::env::temp_dir();
        let pid = std::process::id();
        let normal = dir.join(format!("nabu-sm-{tag}-normal-{pid}.raw"));
        let alert = dir.join(format!("nabu-sm-{tag}-alert-{pid}.raw"));
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
        (normal, alert)
    }

    // A hub-marked alert (timer/alarm) must open the non-attenuated alert sink so it rings at
    // full scale instead of the calibrated conversational voice level.
    #[tokio::test]
    async fn audio_start_marked_alert_routes_to_the_alert_sink() {
        let (normal, alert) = frame_paths("marked");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump_with(&normal, &alert);
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0,"alert":true}),
        );
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&alert).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!normal.exists(), "an alert must not reach the voice sink");
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }

    // Back-compat: a pre-1.5 hub sends audio-start with no `alert` field, and every ordinary
    // reply omits it too. Both must keep the calibrated voice sink.
    #[tokio::test]
    async fn audio_start_without_the_alert_field_routes_to_the_normal_sink() {
        let (normal, alert) = frame_paths("unmarked");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump_with(&normal, &alert);
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0}),
        );
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!alert.exists(), "a reply must not reach the alert sink");
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }

    // A non-boolean `alert` is peer-supplied garbage read on the connection's event path: it must
    // degrade to "not an alert", never panic and drop the satellite mid-turn.
    #[tokio::test]
    async fn audio_start_with_a_non_boolean_alert_routes_to_the_normal_sink() {
        let (normal, alert) = frame_paths("garbage");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump_with(&normal, &alert);
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0,"alert":"yes"}),
        );
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!alert.exists());
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }

    #[test]
    fn ring_rms_excludes_the_detection_gap_chunks() {
        // 3 old chunks at constant amplitude 100 (the wake word), 3 newest silent (the gap):
        // excluding the newest 3 must measure only the wake word.
        let loud: Vec<u8> = (0..1280).flat_map(|_| 100i16.to_le_bytes()).collect();
        let quiet: Vec<u8> = (0..1280).flat_map(|_| 0i16.to_le_bytes()).collect();
        let mut ring: VecDeque<Vec<u8>> = VecDeque::new();
        for _ in 0..3 { ring.push_back(loud.clone()); }
        for _ in 0..3 { ring.push_back(quiet.clone()); }
        let rms = ring_rms(&ring, 3);
        assert!((rms - 100.0).abs() < 0.01, "expected 100.0, got {rms}");
    }

    #[test]
    fn ring_rms_on_short_ring_is_zero() {
        let ring: VecDeque<Vec<u8>> = VecDeque::from(vec![vec![0u8; 2560]; 2]);
        assert_eq!(ring_rms(&ring, 3), 0.0);
    }

    /// The fix for wakes fired out of a near-silent room: the classifier scored high, but the
    /// audio it scored was at the mic's idle floor, so nobody can have said the wake word.
    #[test]
    fn a_fire_quieter_than_the_floor_is_not_a_wake() {
        let quiet = WakeSignal { rms: 349.9, score: 0.97 };
        assert!(!quiet.audible(350.0), "a confident score does not rescue a silent trigger");
        assert!(WakeSignal { rms: 350.0, score: 0.51 }.audible(350.0), "the floor itself passes");
    }

    /// A floor of 0 is the feature switched off, and must stay off even for the 0.0 that
    /// ring_rms reports on a ring shorter than the detection gap.
    #[test]
    fn a_zero_floor_admits_every_fire() {
        let ring: VecDeque<Vec<u8>> = VecDeque::from(vec![vec![0u8; 2560]; 2]);
        let silent = WakeSignal { rms: ring_rms(&ring, 3), score: 0.9 };
        assert!(silent.audible(0.0));
    }

    #[tokio::test]
    async fn wake_turn_sends_run_pipeline_with_wake_metadata() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll,
            Some(WakeSignal { rms: 123.5, score: 0.87 }), None).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e.event_type, "run-pipeline");
        let data = e.data_obj();
        assert_eq!(data["source"], serde_json::json!("wake"));
        assert!((data["wake_rms"].as_f64().unwrap() - 123.5).abs() < 0.01);
        assert!((data["wake_score"].as_f64().unwrap() - 0.87).abs() < 0.001);
    }

    // Protocol 1.7. The hub cannot measure the room itself: its first captured frame is already
    // the turn, so a command that runs on from the wake word leaves its gate estimating the noise
    // floor from the speaker's own voice (6x the room, measured on prod). This satellite hears the
    // room the whole time it is idle, which is the measurement that closes that hole.
    #[tokio::test]
    async fn turn_reports_the_room_level_measured_while_idle() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll,
            Some(WakeSignal { rms: 123.5, score: 0.87 }), Some(64.5)).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        let data = e.data_obj();
        assert!((data["room_rms"].as_f64().unwrap() - 64.5).abs() < 0.01);
    }

    // Absent, not null: a satellite that has not been idle long enough to stand behind a reading
    // says nothing, and the hub falls back to what its own captures learned rather than reading a
    // zero as "the room is silent" and pinning its floor there.
    #[tokio::test]
    async fn turn_without_a_room_measurement_omits_the_field() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll,
            Some(WakeSignal { rms: 123.5, score: 0.87 }), None).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert!(!e.data_obj().contains_key("room_rms"));
    }

    #[tokio::test]
    async fn button_turn_sends_run_pipeline_with_button_source() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll, None, None).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e.event_type, "run-pipeline");
        let data = e.data_obj();
        assert_eq!(data["source"], serde_json::json!("button"));
        assert!(!data.contains_key("wake_rms"));
    }

    // THE zero-lag guarantee: a turn flushes the entire pre-roll buffer to the hub (after
    // run-pipeline) before any live audio, so speech right after the wake word isn't clipped.
    #[tokio::test]
    async fn start_turn_flushes_preroll_before_streaming() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();
        for _ in 0..5 { preroll.push_back(vec![0u8; 2560]); }

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll, None, None).await.unwrap();

        assert_eq!(mode, Mode::Streaming);
        assert!(preroll.is_empty(), "pre-roll must be drained on trigger");
        // One persistent BufReader: read_event re-wraps per call and would drop read-ahead
        // bytes between sequential reads (see codec.rs note) — use the buffered reader here.
        let mut buf = BufReader::new(b);
        assert_eq!(read_event_buffered(&mut buf).await.unwrap().unwrap().event_type, "run-pipeline");
        for _ in 0..5 {
            assert_eq!(read_event_buffered(&mut buf).await.unwrap().unwrap().event_type, "audio-chunk");
        }
    }

    // Wake-path regression: the flushed pre-roll must NOT include the wake word itself —
    // only the detection-latency gap (wake fires ~181 ms after the word ends). Saying
    // "ok nabu" then nothing must not transcribe-and-dispatch "ok nabu".
    #[tokio::test]
    async fn wake_trim_keeps_only_the_detection_gap() {
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();
        for i in 0..13 {
            preroll.push_back(vec![i as u8; 2560]); // 13 chunks ≈ the 1000 ms ring, oldest first
        }
        trim_preroll(&mut preroll, 3);
        assert_eq!(preroll.len(), 3);
        // the NEWEST chunks survive (10, 11, 12), the wake-word audio (older) is dropped
        assert_eq!(preroll[0][0], 10);
        assert_eq!(preroll[2][0], 12);
    }

    #[tokio::test]
    async fn transcript_ends_turn_with_audio_stop_and_rearms() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Streaming;
        let mut phase = LedState::Listening;
        let e = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        assert_eq!(read_event(&mut b).await.unwrap().unwrap().event_type, "audio-stop");
    }

    // Regression for a select! cancellation-safety bug: hub events arriving in fragments
    // while the mic floods chunks caused the in-flight read_event_buffered future to be
    // dropped mid-event, desyncing the stream ("stream did not contain valid UTF-8").
    // The hub side below writes each audio-chunk frame in two halves with a yield between
    // them, so pre-fix the partial-progress drop happens almost surely within ~300 events.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn survives_fragmented_hub_frames_under_mic_flood() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();

        // satellite side: mic floods (always-ready /dev/zero), playback to /dev/null
        let cfg = Config {
            mic_command: "head -c 99999999 /dev/zero".into(),
            snd_command: "cat >/dev/null".into(),
            wake_enabled: false, // detector off: keeps the loop hot on raw I/O
            button: crate::config::ButtonConfig::None,
            led: crate::config::LedConfig::None, // no XVF3800 on USB in CI; keep the log clean
            ..Config::default()
        };
        let sat = tokio::spawn(async move {
            let (sock, _) = listener.accept().await.unwrap();
            let (r, w) = sock.into_split();
            run_connection(r, w, cfg, None, crate::volume::VolumeControl::new(None, None, None, 10)).await
        });

        // hub side: dial in, then stream fragmented audio-chunk frames
        let mut hub = tokio::net::TcpStream::connect(addr).await.unwrap();
        use tokio::io::AsyncWriteExt;
        // 0xAA is never a valid UTF-8 leading byte: a desynced reader that parses payload
        // bytes as a header line reproduces the exact live error ("stream did not contain
        // valid UTF-8") instead of a JSON parse error.
        let payload = vec![0xAAu8; 1280];
        let data = json!({"rate":22050,"width":2,"channels":1});
        let body = serde_json::to_vec(&data).unwrap();
        let header = format!(
            "{{\"type\":\"audio-chunk\",\"data_length\":{},\"payload_length\":{}}}\n",
            body.len(),
            payload.len()
        );
        // Ignore hub-side write errors: pre-fix the satellite tears the connection down
        // mid-stream, and the interesting failure is the satellite's own error below.
        let hub_io = async {
            hub.write_all(b"{\"type\":\"run-satellite\"}\n").await?;
            hub.write_all(b"{\"type\":\"audio-start\",\"data\":{\"rate\":22050,\"width\":2,\"channels\":1}}\n").await?;
            for _ in 0..300 {
                // first half of the frame...
                hub.write_all(header.as_bytes()).await?;
                hub.write_all(&body).await?;
                hub.write_all(&payload[..600]).await?;
                hub.flush().await?;
                // ...window where the read future has partial progress (a real sleep, not
                // yield_now: with 2 workers a yield resumes before the satellite polls mid-frame)
                tokio::time::sleep(std::time::Duration::from_millis(1)).await;
                hub.write_all(&payload[600..]).await?;
            }
            hub.write_all(b"{\"type\":\"audio-stop\",\"data\":{\"timestamp\":0}}\n").await?;
            hub.flush().await?;
            Ok::<(), std::io::Error>(())
        };
        let _ = hub_io.await;
        drop(hub); // clean EOF -> satellite loop should exit Ok

        let result = tokio::time::timeout(std::time::Duration::from_secs(30), sat)
            .await
            .expect("satellite loop hung")
            .unwrap();
        result.expect("connection must survive fragmented frames (no desync error)");
    }

    // THE post-reply re-arm guarantee: audio-stop hands the drain to the playback pump and
    // returns immediately. Blocking here parks the whole select! loop (wake detection, button,
    // mic forwarding) for however long the player takes to drain — ~0.5-2 s after every reply.
    #[tokio::test]
    async fn audio_stop_returns_before_player_drain_completes() {
        let (mut a, _b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (handle, mut done_rx, task) = spawn_pump("cat >/dev/null; sleep 1", "cat >/dev/null; sleep 1");
        let playback = handle;
        let _pump = AbortOnDrop(task);
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let chunk = WyomingEvent::audio_chunk(22050, 2, 1, vec![0u8; 4410]);
        handle_hub_event(chunk, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        let t0 = std::time::Instant::now();
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert!(
            t0.elapsed() < std::time::Duration::from_millis(500),
            "audio-stop must not block on the player drain (took {:?})",
            t0.elapsed()
        );
        // ...but the drain still completes and is reported, carrying the stream's generation.
        let d = done_rx.recv().await.unwrap();
        assert_eq!(d.generation, playback.latest_generation());
        assert!(d.result.is_ok());
        assert!(t0.elapsed() >= std::time::Duration::from_millis(900), "drain really took the player's time");
    }

    #[tokio::test]
    async fn transcript_while_idle_is_a_noop() {
        let (mut a, b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let e = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        // nothing must have been written to the hub
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(crate::wyoming::codec::read_event_buffered(&mut buf).await.unwrap().is_none());
    }

    // Arbitration loss: like transcript it stops streaming and re-arms wake, but SILENTLY —
    // no done cue (the user is talking to another satellite) and the LED goes straight to Idle.
    #[tokio::test]
    async fn pause_satellite_ends_streaming_silently_and_rearms() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, mut led_rx) = watch::channel(LedState::Listening);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Streaming;
        let mut phase = LedState::Listening;

        let e = WyomingEvent::new("pause-satellite");
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert_eq!(read_event(&mut b).await.unwrap().unwrap().event_type, "audio-stop");
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle, "silent abort goes dark, not Thinking");
    }

    #[tokio::test]
    async fn pause_satellite_while_idle_is_a_noop() {
        let (mut a, b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let e = WyomingEvent::new("pause-satellite");
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert!(!led_rx.has_changed().unwrap());
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(crate::wyoming::codec::read_event_buffered(&mut buf).await.unwrap().is_none());
    }

    #[tokio::test]
    async fn turn_lifecycle_publishes_led_states() {
        let (mut a, _b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();

        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll, None, None).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        let voice_stopped = WyomingEvent::new("voice-stopped");
        handle_hub_event(voice_stopped, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);

        let transcript = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(transcript, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle, "transcript ends the turn -> ring goes dark");

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        // the LED goes Idle when the pump reports the drain complete, not at audio-stop
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    // Announcements: audio-start arrives with no preceding turn and must still light the LED.
    #[tokio::test]
    async fn announcement_playback_publishes_speaking_then_idle() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    // A turn that interrupts an announcement must not go dark when the announcement's
    // audio-stop drains: the LED stays on (Listening) for the rest of the turn.
    #[tokio::test]
    async fn audio_stop_during_streaming_turn_keeps_led_listening() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        // announcement starts, then a button turn begins while it plays
        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();
        start_turn(&mut a, &mut mode, &mut phase, &ctx, &mut preroll, None, None).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        // the announcement's audio-stop drains while we are mid-turn
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening, "LED must stay lit mid-turn");
    }

    // A stale drain completion (an older stream finishing after a newer audio-start) must not
    // blank the LED mid-Speaking — the generation gate in apply_drain_done.
    #[tokio::test]
    async fn stale_drain_completion_does_not_blank_led() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening;

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start.clone(), &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        // a second stream starts before the first stream's completion is processed
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let d = done_rx.recv().await.unwrap();
        assert_eq!(d.generation, 1, "completion belongs to the superseded stream");
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert!(!led_rx.has_changed().unwrap(), "stale drain must not blank the LED mid-Speaking");
    }

    #[tokio::test]
    async fn stale_transcript_publishes_no_led_state() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening; // not Streaming -> transcript is stale

        let stale = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(stale, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert!(!led_rx.has_changed().unwrap(), "stale transcript must not touch the LED");
    }

    // The hub endpointed the user's speech and is now processing it. This is now the sole
    // source of the Thinking indicator; the capture must stay open (mode unchanged) because
    // the hub, not the satellite, decides when the stream ends (via transcript).
    #[tokio::test]
    async fn voice_stopped_during_streaming_publishes_thinking() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Streaming;
        let mut phase = LedState::Listening;

        let e = WyomingEvent::new("voice-stopped");
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Streaming, "voice-stopped must not close the capture");
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);
    }

    // The agent answers, calls a tool, then answers again: one hub turn, several reply streams.
    // The gap between them is the agent still working, so the ring must return to Thinking.
    // Returning it to Listening (the DoA look) is wrong twice over — the mic is not the thing
    // the user is waiting on, and with no sound in the gap the DoA ring renders as good as dark.
    #[tokio::test]
    async fn drain_between_reply_segments_restores_thinking() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Streaming;
        let mut phase = LedState::Listening;

        let voice_stopped = WyomingEvent::new("voice-stopped");
        handle_hub_event(voice_stopped, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        // The segment drains, but no transcript has arrived: the turn is still open and the
        // agent is still working on the rest of the answer.
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert_eq!(
            *led_rx.borrow_and_update(), LedState::Thinking,
            "a gap between reply segments is the agent thinking, not the mic listening"
        );
    }

    // The hub reopened the mic for a wake-free follow-up turn. That is the one moment the user
    // can speak without the wake word, so the ring must say so — and keep saying so when the
    // follow-up chime's own stream drains right after.
    #[tokio::test]
    async fn listening_started_returns_the_ring_to_listening() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, mut done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Streaming;
        let mut phase = LedState::Listening;

        let voice_stopped = WyomingEvent::new("voice-stopped");
        handle_hub_event(voice_stopped, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);

        let listening = WyomingEvent::new("listening-started");
        handle_hub_event(listening, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        assert_eq!(mode, Mode::Streaming, "the follow-up window must not touch the capture");
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        // A stream draining after it (the chime, or a late announcement) must leave it Listening.
        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, phase, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);
    }

    #[tokio::test]
    async fn listening_started_while_idle_is_a_noop() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening; // no turn open -> stale, must not light the ring

        let e = WyomingEvent::new("listening-started");
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert!(!led_rx.has_changed().unwrap(), "stale listening-started must not touch the LED");
    }

    #[tokio::test]
    async fn voice_stopped_while_idle_is_a_noop() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let vol = crate::volume::VolumeControl::new(None, None, None, 10);
        let (playback, _done_rx, _pump) = pump();
        let ctx = Ctx { cues: &c, led: &led_tx, volume: &vol, playback: &playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Listening; // not Streaming -> stale, must not touch the LED

        let e = WyomingEvent::new("voice-stopped");
        handle_hub_event(e, &mut mode, &mut phase, None, &mut a, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert!(!led_rx.has_changed().unwrap(), "stale voice-stopped must not touch the LED");
    }

    /// Everything a speaker-volume test needs: a duplex sink for the writer and a probe volume
    /// control that records its wpctl calls instead of running them (the alert hold lives in it).
    struct VolFixture {
        log: Arc<std::sync::Mutex<Vec<String>>>,
        vol: Arc<crate::volume::VolumeControl>,
        cues: Cues,
        led: watch::Sender<LedState>,
        // Keeping the receiver alive (rather than std::mem::forget-ing it) is what keeps the
        // watch channel open for the whole test — a dropped receiver would fail led.send.
        _led_rx: watch::Receiver<LedState>,
    }

    fn vol_fixture() -> VolFixture {
        let (log, vol) = crate::volume::VolumeControl::probe_pair(10);
        let (led, led_rx) = watch::channel(LedState::Idle);
        VolFixture { log, vol, cues: cues(), led, _led_rx: led_rx }
    }

    async fn feed(f: &VolFixture, playback: &PlaybackHandle, action: &str) {
        feed_data(f, playback, json!({ "action": action })).await;
    }

    // Drives handle_hub_event with an arbitrary speaker-volume `data` payload, for covering the
    // defensive read itself (missing key / wrong type) rather than a valid-but-unknown action.
    async fn feed_data(f: &VolFixture, playback: &PlaybackHandle, data: serde_json::Value) {
        let (mut a, _b) = tokio::io::duplex(4096);
        let ctx = Ctx { cues: &f.cues, led: &f.led, volume: &f.vol, playback };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Idle;
        let event = WyomingEvent::with_data("speaker-volume", data);
        handle_hub_event(event, &mut mode, &mut phase, None, &mut a, &ctx)
            .await
            .unwrap();
    }

    // The mute is deliberately applied only after the confirmation cue has drained, in a detached
    // task, so it cannot silence its own cue — hence the poll rather than a bare assert.
    async fn wait_for_mute(vol: &Arc<crate::volume::VolumeControl>, expected: bool) {
        for _ in 0..200 {
            if vol.user_muted() == expected {
                return;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        panic!("mute never became {expected}");
    }

    #[tokio::test]
    async fn speaker_volume_mute_then_unmute_tracks_the_users_intent() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "mute").await;
        wait_for_mute(&f.vol, true).await;

        feed(&f, &playback, "unmute").await;
        assert!(!f.vol.user_muted());

        let calls = f.log.lock().unwrap().clone();
        assert!(calls.iter().any(|c| c.contains("set-mute") && c.ends_with('1')), "got {calls:?}");
        assert!(calls.iter().any(|c| c.contains("set-mute") && c.ends_with('0')), "got {calls:?}");
    }

    // The probe's canned read is 0.65 (−11.2 dB, snapping to the −10.2 dB grid point), so a step
    // up writes the −5.1 dB level and a step down the −15.3 dB one — the equal-dB grid, not a
    // relative % bump (each feed re-reads the same canned 0.65).
    #[tokio::test]
    async fn speaker_volume_up_and_down_step_the_sink() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "up").await;
        feed(&f, &playback, "down").await;

        let calls = f.log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2, "got {calls:?}");
        assert!(calls[0].contains("set-volume") && calls[0].ends_with("0.8222"), "got {}", calls[0]);
        assert!(calls[1].contains("set-volume") && calls[1].ends_with("0.5559"), "got {}", calls[1]);
    }

    // An alarm must ring even on a muted speaker, and the user's mute must come back afterwards.
    // The hold unmutes the SINK without clearing the user's intent — otherwise the release would
    // have nothing to restore and a dismissed alarm would leave the speaker permanently unmuted.
    #[tokio::test]
    async fn alert_hold_unmutes_and_release_restores_the_users_mute() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        f.vol.set_user_mute(true).await.unwrap();
        f.log.lock().unwrap().clear();

        feed(&f, &playback, "alert-hold").await;
        assert!(f.vol.user_muted(), "the hold must not clear the user's intent");
        assert!(f.vol.alert_held());
        assert!(
            f.log.lock().unwrap().last().unwrap().ends_with('0'),
            "the sink is unmuted for the ring"
        );

        feed(&f, &playback, "alert-release").await;
        assert!(!f.vol.alert_held());
        assert!(
            f.log.lock().unwrap().last().unwrap().ends_with('1'),
            "the user's mute comes back after the alarm"
        );
    }

    // A mute that arrives while an alarm is ringing must not silence the alarm. The user's intent
    // is recorded straight away and applied to the sink only when the hold is released.
    #[tokio::test]
    async fn mute_during_an_alert_hold_is_deferred_until_the_release() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "alert-hold").await;
        f.log.lock().unwrap().clear();

        feed(&f, &playback, "mute").await;
        wait_for_mute(&f.vol, true).await;
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        assert!(
            f.log.lock().unwrap().is_empty(),
            "a deferred mute must not touch the sink while an alarm rings: {:?}",
            f.log.lock().unwrap()
        );

        feed(&f, &playback, "alert-release").await;
        let calls = f.log.lock().unwrap().clone();
        assert_eq!(calls, vec!["wpctl set-mute SINK 1".to_string()], "the mute lands on the release");
    }

    // The guard exists so a hub dying mid-alarm cannot leave the speaker silently muted. With a
    // deferred mute outstanding it has to go the other way too: apply what the user asked for.
    #[tokio::test]
    async fn dropping_the_hold_guard_restores_the_users_intent() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "alert-hold").await;
        feed(&f, &playback, "mute").await;
        wait_for_mute(&f.vol, true).await;
        f.log.lock().unwrap().clear();

        drop(HoldGuard { volume: f.vol.clone() });

        let calls = f.log.lock().unwrap().clone();
        assert_eq!(calls, vec!["wpctl set-mute SINK 1".to_string()], "teardown applies the user's mute");
    }

    // A stray or duplicated release must not be able to change the mute state on its own.
    #[tokio::test]
    async fn alert_release_without_a_hold_is_a_no_op() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "alert-release").await;

        assert!(f.log.lock().unwrap().is_empty(), "a release with no hold must write nothing");
    }

    // A newer hub must never be able to drop an older satellite's connection.
    #[tokio::test]
    async fn unknown_speaker_volume_action_is_ignored() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed(&f, &playback, "teleport").await;

        assert!(f.log.lock().unwrap().is_empty());
        assert!(!f.vol.user_muted());
    }

    // The defensive read (`.get("action").and_then(|v| v.as_str()).unwrap_or("")`) exists for
    // exactly this: a peer-supplied event on the connection's event path with no `action` key at
    // all must not panic and drop the satellite, and must fall through to the same ignored path
    // as an unrecognized action.
    #[tokio::test]
    async fn speaker_volume_event_missing_the_action_key_is_ignored() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed_data(&f, &playback, json!({})).await;

        assert!(f.log.lock().unwrap().is_empty());
        assert!(!f.vol.user_muted());
    }

    // Same defensive read, the other failure shape: `action` present but not a string (e.g. a
    // hub-side encoding bug sending a number). `.as_str()` returns None here too, so this must
    // degrade exactly like the missing-key case, never panic.
    #[tokio::test]
    async fn speaker_volume_event_with_a_non_string_action_is_ignored() {
        let f = vol_fixture();
        let (playback, _done_rx, _pump) = pump();

        feed_data(&f, &playback, json!({ "action": 5 })).await;

        assert!(f.log.lock().unwrap().is_empty());
        assert!(!f.vol.user_muted());
    }
}
