use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::sync::Mutex;
use tracing::warn;

/// The satellite's own MASTER output level: the one knob that music, replies, cues and alerts all
/// pass through. This is deliberately not one of the per-source ALSA softvols (`Music` / `TTS` /
/// `Alert`) — those carry calibration, and `Music` is written by the ducker on every turn. Driving
/// the master keeps the two independent: they simply multiply.
///
/// Two backends, the same master, different tools — see `Backend`.
///
/// `gate` serializes `step`/`alert_hold`/`alert_release`/`set_user_mute` end-to-end (decision
/// through the awaited mixer call). Without it, `set_user_mute`'s read-modify-store of
/// `user_muted` and its awaited master write are two separate windows a concurrent call can land
/// inside — e.g. an alert hold racing a queued "mute" confirmation, which can leave `user_muted()`
/// disagreeing with whichever call's process actually landed last on the real master.
/// `tokio::sync::Mutex`, not `std::sync::Mutex`, because the held region awaits.
///
/// `alert_held` lives here rather than in the connection's `Ctx` because every decision reads it
/// together with `user_muted`, which is process-scoped: keeping them apart meant `set_user_mute`
/// could not see a hold, so a mute arriving ~100 ms after an alarm started silenced the alarm.
/// Under the same gate, the two are always read and written as one consistent pair.
pub struct VolumeControl {
    backend: Backend,
    step: u8,
    user_muted: AtomicBool,
    alert_held: AtomicBool,
    gate: Mutex<()>,
}

/// What drives the master, which depends only on what is installed on the unit — the gate, the
/// tracked mute and the alert hold above are identical either way.
///
/// Music units mix everything in PipeWire, so their master is its sink and `wpctl` moves it.
/// Voice-only units have no PipeWire (installing it just for a volume knob would pull a sound
/// server onto a unit that plays raw ALSA), so provisioning puts a software softvol in front of
/// their output device and `amixer` moves that. A software level is not a shortcut on this
/// hardware: an amp HAT like the MiniAmp has no hardware volume control at all.
enum Backend {
    /// Neither flag given: nothing to drive, so every command is a warned no-op.
    Disabled,
    Pipewire { sink: String },
    Alsa { control: String, card: Option<String> },
    /// Records the command line a real backend would have run, without running it.
    #[cfg(test)]
    Probe { inner: Box<Backend>, log: Arc<std::sync::Mutex<Vec<String>>>, capture: Option<String> },
    #[cfg(test)]
    Failing,
}

/// What a public method wants done, so the two real backends differ only in how they spell it.
#[derive(Clone, Copy)]
enum Op {
    Step { up: bool },
    Mute(bool),
}

impl Backend {
    /// The command line that applies `op`, or None when there is nothing to drive.
    fn cmdline(&self, op: Op, step: u8) -> Option<String> {
        match self {
            Backend::Disabled => None,
            // wpctl spells mute as a 0/1 argument. Steps never come through here: `step` takes
            // the read → dB-snap → absolute-write path for this backend (see `step_read_query`).
            Backend::Pipewire { sink } => match op {
                Op::Step { .. } => None,
                Op::Mute(muted) => Some(format!("wpctl set-mute {sink} {}", u8::from(muted))),
            },
            // amixer clamps a relative step to the control's own range, so repeated steps up
            // settle at 100% instead of overshooting. The softvol's taper is already linear in
            // dB, so the relative step IS the equal-dB step the PipeWire backend computes by
            // hand. A softvol carries no switch of its own, so provisioning pairs the `<name>
            // Volume` element with a `<name> Switch` one; ALSA's simple mixer merges the two into
            // this single control, which is why mute is the same `sset` the level uses.
            Backend::Alsa { control, card } => {
                let card = card.as_ref().map(|c| format!(" -c {c}")).unwrap_or_default();
                Some(match op {
                    Op::Step { up } => format!("amixer{card} sset {control} {step}%{}", sign(up)),
                    Op::Mute(muted) => {
                        format!("amixer{card} sset {control} {}", if muted { "mute" } else { "unmute" })
                    }
                })
            }
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.cmdline(op, step),
            #[cfg(test)]
            Backend::Failing => Some("false".into()),
        }
    }

    /// The read that must precede a step, or None when a blind relative command is enough.
    /// PipeWire's display scale is cubic, so an equal-dB step needs the current level first;
    /// the ALSA softvol's taper is already dB-linear, so its relative `amixer` step needs no read.
    fn step_read_query(&self) -> Option<String> {
        match self {
            Backend::Pipewire { .. } => self.query_cmdline(),
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.step_read_query(),
            _ => None,
        }
    }

    /// The absolute write that lands a computed level, for the backends whose steps read first.
    fn set_volume_cmdline(&self, value: f64) -> Option<String> {
        match self {
            Backend::Pipewire { sink } => Some(format!("wpctl set-volume {sink} {value:.4}")),
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.set_volume_cmdline(value),
            _ => None,
        }
    }

    /// The command line that PRINTS the current mute state, for `seed`.
    fn query_cmdline(&self) -> Option<String> {
        match self {
            Backend::Disabled => None,
            Backend::Pipewire { sink } => Some(format!("wpctl get-volume {sink}")),
            Backend::Alsa { control, card } => {
                let card = card.as_ref().map(|c| format!(" -c {c}")).unwrap_or_default();
                Some(format!("amixer{card} sget {control}"))
            }
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.query_cmdline(),
            #[cfg(test)]
            Backend::Failing => Some("false".into()),
        }
    }

    /// Whether that output says the master is muted. Anything unrecognized reads as audible.
    fn reads_muted(&self, out: &str) -> bool {
        match self {
            Backend::Pipewire { .. } => out.contains("[MUTED]"),
            Backend::Alsa { .. } => out.contains("[off]"),
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.reads_muted(out),
            _ => false,
        }
    }

    /// What the startup log prints, so a unit driving nothing says so in its journal.
    fn describe(&self) -> String {
        match self {
            Backend::Disabled => "disabled".into(),
            Backend::Pipewire { sink } => format!("pipewire sink {sink}"),
            Backend::Alsa { control, card: Some(card) } => format!("alsa control {control} on card {card}"),
            Backend::Alsa { control, card: None } => format!("alsa control {control}"),
            #[cfg(test)]
            Backend::Probe { inner, .. } => inner.describe(),
            #[cfg(test)]
            Backend::Failing => "failing".into(),
        }
    }

    async fn capture(&self, cmdline: &str) -> anyhow::Result<String> {
        match self {
            #[cfg(test)]
            Backend::Probe { capture, .. } => {
                capture.clone().ok_or_else(|| anyhow::anyhow!("probe read failed"))
            }
            #[cfg(test)]
            Backend::Failing => anyhow::bail!("volume read failed"),
            _ => run_capture(cmdline).await,
        }
    }
}

fn sign(up: bool) -> char {
    if up { '+' } else { '-' }
}

/// The bottom of the stepping range, matching the voice-only softvol's taper: quiet, never
/// silent. Only `mute` silences a speaker.
const FLOOR_DB: f64 = -51.0;

/// One dB-linear step on the PipeWire sink's cubic display scale (0.0..=1.0), so a spoken step
/// moves the same number of dB as `amixer` does on the voice-only softvol: 51·step/100 dB
/// (5.1 dB at the default 10). wpctl's display value maps to `dB = 60·log10(v)` — the pulse-style
/// cubic curve — which makes a constant-dB step pure math on the read value. The current level is
/// snapped to a grid anchored at 0 dB before stepping, so repeated commands never drift and an
/// externally-moved sink heals to the grid on the first command. A level at or below silence
/// clamps to the floor, so a step up from a zeroed sink is immediately audible.
fn db_linear_step(current: f64, up: bool, step_pct: u8) -> f64 {
    let spacing = -FLOOR_DB * f64::from(step_pct) / 100.0;
    let db = if current > 0.0 { (60.0 * current.log10()).clamp(FLOOR_DB, 0.0) } else { FLOOR_DB };
    let steps_below_full = (-db / spacing).round() + if up { -1.0 } else { 1.0 };
    10f64.powf((-steps_below_full * spacing).clamp(FLOOR_DB, 0.0) / 60.0)
}

/// The level out of `wpctl get-volume` ("Volume: 0.65", possibly with a trailing "[MUTED]").
fn parse_wpctl_volume(out: &str) -> anyhow::Result<f64> {
    out.split_whitespace()
        .skip_while(|token| *token != "Volume:")
        .nth(1)
        .and_then(|token| token.parse().ok())
        .ok_or_else(|| anyhow::anyhow!("unrecognized wpctl get-volume output: {out:?}"))
}

impl VolumeControl {
    /// `sink` is the PipeWire master (music units), `mixer`/`card` the ALSA one (voice-only).
    /// `Config::parse` rejects a unit that gives both, so the order below is only a tiebreak.
    pub fn new(sink: Option<String>, mixer: Option<String>, card: Option<String>, step: u8) -> Arc<Self> {
        let backend = match (sink, mixer) {
            (Some(sink), _) => Backend::Pipewire { sink },
            (None, Some(control)) => Backend::Alsa { control, card },
            (None, None) => Backend::Disabled,
        };
        Arc::new(Self::with_backend(backend, step))
    }

    fn with_backend(backend: Backend, step: u8) -> Self {
        Self {
            backend,
            step,
            user_muted: AtomicBool::new(false),
            alert_held: AtomicBool::new(false),
            gate: Mutex::new(()),
        }
    }

    /// pub(crate) so state_machine's tests can drive a real control without a wpctl binary.
    /// Carries a canned sink read (0.65, snapping to −10.2 dB) because a PipeWire step reads
    /// before it writes: one step up lands 0.8222, one step down 0.5559.
    #[cfg(test)]
    pub(crate) fn probe_pair(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<Self>) {
        Self::probe_backend(Backend::Pipewire { sink: "SINK".into() }, step, Some("Volume: 0.65\n"))
    }

    /// A control that logs what `inner` would have run. `capture` is what a `seed` read returns;
    /// None makes that read fail.
    #[cfg(test)]
    fn probe_backend(
        inner: Backend,
        step: u8,
        capture: Option<&str>,
    ) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<Self>) {
        let log = Arc::new(std::sync::Mutex::new(Vec::new()));
        let backend = Backend::Probe {
            inner: Box::new(inner),
            log: log.clone(),
            capture: capture.map(str::to_owned),
        };
        (log, Arc::new(Self::with_backend(backend, step)))
    }

    #[cfg(test)]
    fn failing(step: u8) -> Arc<Self> {
        Arc::new(Self::with_backend(Backend::Failing, step))
    }

    pub fn enabled(&self) -> bool {
        !matches!(self.backend, Backend::Disabled)
    }

    pub fn describe(&self) -> String {
        self.backend.describe()
    }

    pub fn user_muted(&self) -> bool {
        self.user_muted.load(Ordering::SeqCst)
    }

    pub fn alert_held(&self) -> bool {
        self.alert_held.load(Ordering::SeqCst)
    }

    /// Read the master's current mute once at startup so the satellite's idea of the user's intent
    /// matches what the mixer restored. A failed or unparsable read leaves it unmuted, which is
    /// the safe direction: the speaker is audible and one spoken command fixes it.
    pub async fn seed(&self) {
        let Some(query) = self.backend.query_cmdline() else { return };
        match self.backend.capture(&query).await {
            Ok(out) => {
                let muted = self.backend.reads_muted(&out);
                self.user_muted.store(muted, Ordering::SeqCst);
                tracing::info!(muted, "seeded local volume mute state");
            }
            Err(e) => warn!("could not read master mute state, assuming unmuted: {e:#}"),
        }
    }

    /// One equal-dB step, clamped to the −51..0 dB range on both backends, so the same spoken
    /// command moves the same loudness on a music and a voice-only unit. The ALSA softvol's taper
    /// is dB-linear, so its relative `amixer` command is already that step; the PipeWire scale is
    /// cubic, so the sink is read first and the snapped next grid level written back absolutely
    /// (see `db_linear_step`). Both the read and the write run under the gate, so a step can't
    /// interleave with a mute's own read-modify-store-await sequence. A failed or unparsable read
    /// errors out — warn, no cue — rather than guessing a level.
    pub async fn step(&self, up: bool) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        let Some(query) = self.backend.step_read_query() else {
            return self.run(Op::Step { up }).await;
        };
        let out = self.backend.capture(&query).await?;
        let target = db_linear_step(parse_wpctl_volume(&out)?, up, self.step);
        let Some(cmdline) = self.backend.set_volume_cmdline(target) else { return Ok(()) };
        self.run_cmdline(cmdline).await
    }

    /// The actual master write. Callers take the gate themselves and then call this, so the gate is
    /// taken exactly once per public call — re-locking an already-held `tokio::sync::Mutex` would
    /// deadlock, since it is not reentrant.
    async fn set_sink_mute_locked(&self, muted: bool) -> anyhow::Result<()> {
        self.run(Op::Mute(muted)).await
    }

    /// An alarm must ring even on a muted speaker: mark the hold and make sure the sink is
    /// audible, without clearing the user's intent — the release has to have something to restore.
    ///
    /// Idempotent, because the hub re-sends the hold on every ring round (that is what heals a
    /// satellite which reconnected mid-alarm, or was rebooting when the alarm started). A repeat
    /// while the hold already stands writes nothing. A hold whose sink write FAILS un-marks
    /// itself, so the next round's re-assert retries instead of skipping.
    pub async fn alert_hold(&self) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        if self.alert_held.swap(true, Ordering::SeqCst) {
            return Ok(());
        }
        if self.user_muted() {
            if let Err(e) = self.set_sink_mute_locked(false).await {
                self.alert_held.store(false, Ordering::SeqCst);
                return Err(e);
            }
        }
        Ok(())
    }

    /// Ends the hold and puts the sink back to whatever the user asked for — including a mute that
    /// arrived DURING the alarm and was deliberately not applied then. A release with no hold
    /// outstanding writes nothing, so a stray or duplicated one cannot mute a speaker on its own.
    pub async fn alert_release(&self) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        if !self.alert_held.swap(false, Ordering::SeqCst) {
            return Ok(());
        }
        self.set_sink_mute_locked(self.user_muted()).await
    }

    /// Records the user's intent and, unless an alert hold is outstanding, applies it to the sink.
    /// A mute during a ringing alarm is DEFERRED — recorded now, written by `alert_release` — so a
    /// "silencia el altavoz" said a second before a timer fires cannot silence that timer. An
    /// unmute always lands at once: it is harmless mid-alarm and is exactly what was asked for.
    ///
    /// A failed write rolls the intent back, so a mute that never landed cannot be re-applied
    /// later by an alert release. Holds the gate for the whole read-modify-await-store sequence,
    /// so a concurrent call cannot observe or land in the middle of it — see the struct's docs.
    pub async fn set_user_mute(&self, muted: bool) -> anyhow::Result<()> {
        if !self.enabled() {
            warn!("local mute ignored: neither --volume-sink nor --volume-mixer configured");
            return Ok(()); // track nothing, so a later alert release has nothing to restore
        }

        let _guard = self.gate.lock().await;
        let previous = self.user_muted();
        self.user_muted.store(muted, Ordering::SeqCst);
        if muted && self.alert_held() {
            return Ok(());
        }
        if let Err(e) = self.set_sink_mute_locked(muted).await {
            self.user_muted.store(previous, Ordering::SeqCst);
            return Err(e);
        }
        Ok(())
    }

    /// Fail-safe teardown for Drop, which cannot await: end the hold and fire a detached std
    /// mixer call putting the master back to the user's intent, never awaited. Same shape as
    /// music.rs's DuckGuard restore, and for the same reason. It runs both ways — a hub that dies
    /// mid-alarm must not leave the speaker silently muted, and must not swallow a mute deferred
    /// by the hold either. Deliberately synchronous and lock-free: `gate` is an async mutex and
    /// `Drop::drop` has no executor to await it against, so this cannot take part in the
    /// serialization the other methods get. It is best-effort, firing after the connection is
    /// going away regardless, not a command that needs to serialize against anything running.
    pub fn release_hold_detached(&self) {
        if !self.alert_held.swap(false, Ordering::SeqCst) {
            return;
        }
        let muted = self.user_muted();
        let Some(cmdline) = self.backend.cmdline(Op::Mute(muted), self.step) else { return };
        match &self.backend {
            #[cfg(test)]
            Backend::Probe { log, .. } => log.lock().unwrap().push(cmdline),
            _ => spawn_detached(&cmdline),
        }
    }

    async fn run(&self, op: Op) -> anyhow::Result<()> {
        #[cfg(test)]
        if let Backend::Failing = self.backend {
            anyhow::bail!("volume command failed");
        }
        let Some(cmdline) = self.backend.cmdline(op, self.step) else {
            warn!("local volume command ignored: neither --volume-sink nor --volume-mixer configured");
            return Ok(());
        };
        #[cfg(test)]
        if let Backend::Probe { .. } = &self.backend {
            // A real mixer call is a subprocess with variable OS-scheduling latency, so two
            // overlapping calls can complete in either order regardless of which one started
            // (and stored its bookkeeping) first. Give a "mute on" command a few extra yields
            // so a concurrency test can force that inversion deterministically on the
            // single-threaded test runtime, without any real sleeping — see
            // `concurrent_set_user_mute_calls_serialize_and_stay_consistent`.
            let extra_yields = if matches!(op, Op::Mute(true)) { 3 } else { 1 };
            for _ in 0..extra_yields {
                tokio::task::yield_now().await;
            }
        }
        self.run_cmdline(cmdline).await
    }

    /// Executes (or, on the probe backend, records) one already-built mixer command line.
    async fn run_cmdline(&self, cmdline: String) -> anyhow::Result<()> {
        #[cfg(test)]
        if let Backend::Probe { log, .. } = &self.backend {
            log.lock().unwrap().push(cmdline);
            return Ok(());
        }
        let status = crate::audio::build_command(&cmdline)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .await?;
        anyhow::ensure!(status.success(), "`{cmdline}` exited with {status}");
        Ok(())
    }
}

/// Fire-and-forget a mixer command from a non-async context. Both backends' command lines are
/// plain argv with no shell metacharacters, so a whitespace split is the whole parse.
fn spawn_detached(cmdline: &str) {
    let mut argv = cmdline.split_whitespace();
    let Some(program) = argv.next() else { return };
    let mut cmd = std::process::Command::new(program);
    cmd.args(argv);
    crate::audio::run_detached(cmd);
}

async fn run_capture(cmdline: &str) -> anyhow::Result<String> {
    let out = crate::audio::build_command(cmdline)
        .stderr(std::process::Stdio::null())
        .output()
        .await?;
    anyhow::ensure!(out.status.success(), "command exited with {}", out.status);
    Ok(String::from_utf8_lossy(&out.stdout).into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn probe(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<VolumeControl>) {
        VolumeControl::probe_pair(step)
    }

    /// Loudness math parity with the voice-only softvol: the display value is PipeWire's cubic
    /// scale (`dB = 60·log10(v)`), and one step moves 51·step/100 dB on a grid anchored at 0 dB.
    mod db_linear_step_math {
        use super::super::db_linear_step;

        fn assert_close(actual: f64, expected: f64) {
            assert!((actual - expected).abs() < 1e-3, "expected ~{expected}, got {actual}");
        }

        /// The default step (10) is 5.1 dB, the same jump the softvol makes: 10^(-5.1/60).
        #[test]
        fn one_step_down_from_full_is_5_1_db() {
            assert_close(db_linear_step(1.0, false, 10), 0.82224);
        }

        /// 0 dB is the ceiling — the math clamp that replaces wpctl's `-l 1.0`.
        #[test]
        fn step_up_from_full_stays_at_full() {
            assert_close(db_linear_step(1.0, true, 10), 1.0);
        }

        /// −51 dB is the floor, matching the softvol: quiet, never silent. Only mute silences.
        #[test]
        fn step_down_from_the_floor_stays_at_the_floor() {
            let floor = 10f64.powf(-51.0 / 60.0); // ≈ 0.141254
            assert_close(db_linear_step(floor, false, 10), floor);
        }

        /// An externally-moved sink snaps to the grid on the first command: 0.5 is −18.06 dB,
        /// nearest grid point −20.4 dB (k=4), so up lands on −15.3 dB and down on −25.5 dB.
        #[test]
        fn off_grid_level_snaps_to_the_grid_before_stepping() {
            assert_close(db_linear_step(0.5, true, 10), 0.55590);
            assert_close(db_linear_step(0.5, false, 10), 0.37584);
        }

        /// A sink someone zeroed externally: −∞ dB clamps to the floor, so the first step up
        /// lands one step above it rather than staying inaudible forever.
        #[test]
        fn step_up_from_silence_lands_one_step_above_the_floor() {
            assert_close(db_linear_step(0.0, true, 10), 0.17179); // −45.9 dB
            assert_close(db_linear_step(0.0, false, 10), 10f64.powf(-51.0 / 60.0));
        }

        /// The per-step dB scales with --volume-step: 50 → two steps across the range, 25.5 dB each.
        #[test]
        fn a_non_default_step_scales_the_grid() {
            assert_close(db_linear_step(1.0, false, 50), 0.37584); // −25.5 dB
        }
    }

    /// The read side of a PipeWire step: what `wpctl get-volume` prints, level extracted, mute
    /// marker tolerated, anything unrecognizable an error (never a guessed level).
    mod parse_wpctl_volume_output {
        use super::super::parse_wpctl_volume;

        #[test]
        fn reads_the_level() {
            assert_eq!(parse_wpctl_volume("Volume: 0.65\n").unwrap(), 0.65);
        }

        /// A muted sink still reports its stored level; stepping while muted must see it.
        #[test]
        fn reads_the_level_of_a_muted_sink() {
            assert_eq!(parse_wpctl_volume("Volume: 0.65 [MUTED]\n").unwrap(), 0.65);
        }

        #[test]
        fn garbage_is_an_error_not_a_guessed_level() {
            assert!(parse_wpctl_volume("Object '99' not found\n").is_err());
            assert!(parse_wpctl_volume("Volume: loud\n").is_err());
            assert!(parse_wpctl_volume("").is_err());
        }
    }

    /// The voice-only shape: an ALSA softvol master driven with `amixer`, on the speaker card.
    fn alsa_probe(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<VolumeControl>) {
        let backend = Backend::Alsa { control: "Master".into(), card: Some("hat".into()) };
        VolumeControl::probe_backend(backend, step, None)
    }

    #[tokio::test]
    async fn alsa_step_up_and_down_issue_relative_amixer_calls() {
        let (log, vol) = alsa_probe(10);
        vol.step(true).await.unwrap();
        vol.step(false).await.unwrap();
        assert_eq!(
            log.lock().unwrap().clone(),
            vec![
                "amixer -c hat sset Master 10%+".to_string(),
                "amixer -c hat sset Master 10%-".to_string(),
            ]
        );
    }

    /// Without a card the control is looked up on ALSA's default card, mirroring `--music-card`.
    #[tokio::test]
    async fn alsa_without_a_card_omits_the_c_flag() {
        let backend = Backend::Alsa { control: "Master".into(), card: None };
        let (log, vol) = VolumeControl::probe_backend(backend, 5, None);
        vol.step(true).await.unwrap();
        assert_eq!(log.lock().unwrap().clone(), vec!["amixer sset Master 5%+".to_string()]);
    }

    #[tokio::test]
    async fn alsa_mute_and_unmute_drive_the_amixer_switch() {
        let (log, vol) = alsa_probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.set_user_mute(false).await.unwrap();
        assert_eq!(
            log.lock().unwrap().clone(),
            vec![
                "amixer -c hat sset Master mute".to_string(),
                "amixer -c hat sset Master unmute".to_string(),
            ]
        );
    }

    /// `amixer sget` prints `[off]` for a muted switch and `[on]` for an audible one.
    #[tokio::test]
    async fn alsa_seed_reads_the_switch_state() {
        let muted = "Simple mixer control 'Master',0\n  Mono: Playback 168 [66%] [-17.00dB] [off]\n";
        let backend = Backend::Alsa { control: "Master".into(), card: Some("hat".into()) };
        let (_, vol) = VolumeControl::probe_backend(backend, 10, Some(muted));
        vol.seed().await;
        assert!(vol.user_muted());

        let audible = "Simple mixer control 'Master',0\n  Mono: Playback 255 [100%] [0.00dB] [on]\n";
        let backend = Backend::Alsa { control: "Master".into(), card: Some("hat".into()) };
        let (_, vol) = VolumeControl::probe_backend(backend, 10, Some(audible));
        vol.seed().await;
        assert!(!vol.user_muted());
    }

    /// A read that succeeds but says nothing recognizable must leave the speaker audible — the
    /// safe direction, exactly as the PipeWire path already treats an unparsable `wpctl` read.
    #[tokio::test]
    async fn alsa_seed_leaves_unmuted_on_a_garbage_read() {
        let backend = Backend::Alsa { control: "Master".into(), card: Some("hat".into()) };
        let (_, vol) = VolumeControl::probe_backend(backend, 10, Some("Unable to find simple control"));
        vol.seed().await;
        assert!(!vol.user_muted());
    }

    /// A read that FAILS outright (no amixer, no such control) must not be taken as a mute.
    #[tokio::test]
    async fn seed_leaves_unmuted_when_the_read_fails() {
        let vol = VolumeControl::failing(10);
        vol.seed().await;
        assert!(!vol.user_muted());
    }

    #[tokio::test]
    async fn pipewire_seed_reads_the_muted_marker() {
        let backend = Backend::Pipewire { sink: "SINK".into() };
        let (_, vol) = VolumeControl::probe_backend(backend, 10, Some("Volume: 0.65 [MUTED]\n"));
        vol.seed().await;
        assert!(vol.user_muted());

        let backend = Backend::Pipewire { sink: "SINK".into() };
        let (_, vol) = VolumeControl::probe_backend(backend, 10, Some("Volume: 0.65\n"));
        vol.seed().await;
        assert!(!vol.user_muted());
    }

    /// THE guarantee the two Critical fixes established, now on the ALSA backend: a mute spoken
    /// while an alarm is ringing is recorded and applied at the release, never written under the
    /// hold — otherwise it silences the alarm it arrived during.
    #[tokio::test]
    async fn alsa_mute_during_an_alert_hold_is_deferred_to_the_release() {
        let (log, vol) = alsa_probe(10);
        vol.alert_hold().await.unwrap();

        vol.set_user_mute(true).await.unwrap();
        assert!(vol.user_muted(), "the intent is recorded even though nothing is written");
        assert!(log.lock().unwrap().is_empty(), "a mute must not silence a ringing alarm");

        vol.alert_release().await.unwrap();
        assert_eq!(log.lock().unwrap().clone(), vec!["amixer -c hat sset Master mute".to_string()]);
    }

    /// Both real backends are enabled; only an unconfigured one is off. The description is what
    /// the startup log prints, so a unit that drives nothing says so in its journal.
    #[test]
    fn the_configured_flags_pick_the_backend() {
        let pipewire = VolumeControl::new(Some("@DEFAULT_AUDIO_SINK@".into()), None, None, 10);
        assert!(pipewire.enabled());
        assert_eq!(pipewire.describe(), "pipewire sink @DEFAULT_AUDIO_SINK@");

        let alsa = VolumeControl::new(None, Some("Master".into()), Some("hat".into()), 10);
        assert!(alsa.enabled());
        assert_eq!(alsa.describe(), "alsa control Master on card hat");

        let alsa_default_card = VolumeControl::new(None, Some("Master".into()), None, 10);
        assert!(alsa_default_card.enabled());
        assert_eq!(alsa_default_card.describe(), "alsa control Master");

        let off = VolumeControl::new(None, None, None, 10);
        assert!(!off.enabled());
        assert_eq!(off.describe(), "disabled");
    }

    /// A PipeWire step is read → snap to the dB grid → absolute write, so it moves the same
    /// number of dB as the softvol's relative step does on a voice-only unit. 0.65 is −11.2 dB,
    /// which snaps to the −10.2 dB grid point; one step up lands on −5.1 dB (0.8222) and one
    /// step down on −15.3 dB (0.5559).
    #[tokio::test]
    async fn pipewire_step_reads_the_sink_and_writes_the_next_grid_level() {
        let backend = Backend::Pipewire { sink: "SINK".into() };
        let (log, vol) = VolumeControl::probe_backend(backend, 10, Some("Volume: 0.65\n"));
        vol.step(true).await.unwrap();
        vol.step(false).await.unwrap();
        assert_eq!(
            log.lock().unwrap().clone(),
            vec![
                "wpctl set-volume SINK 0.8222".to_string(),
                "wpctl set-volume SINK 0.5559".to_string(),
            ]
        );
    }

    /// The 0 dB ceiling in the math replaces wpctl's `-l 1.0` limit flag.
    #[tokio::test]
    async fn pipewire_step_up_at_full_stays_at_full() {
        let backend = Backend::Pipewire { sink: "SINK".into() };
        let (log, vol) = VolumeControl::probe_backend(backend, 10, Some("Volume: 1.00\n"));
        vol.step(true).await.unwrap();
        assert_eq!(log.lock().unwrap().clone(), vec!["wpctl set-volume SINK 1.0000".to_string()]);
    }

    /// A step that cannot read the sink must not guess a level: error out (warn, no cue) and
    /// write nothing.
    #[tokio::test]
    async fn pipewire_step_with_a_failed_read_writes_nothing() {
        let backend = Backend::Pipewire { sink: "SINK".into() };
        let (log, vol) = VolumeControl::probe_backend(backend, 10, None);
        assert!(vol.step(true).await.is_err());
        assert!(log.lock().unwrap().is_empty());
    }

    #[tokio::test]
    async fn set_user_mute_tracks_the_users_intent() {
        let (_, vol) = probe(10);
        assert!(!vol.user_muted());
        vol.set_user_mute(true).await.unwrap();
        assert!(vol.user_muted());
        vol.set_user_mute(false).await.unwrap();
        assert!(!vol.user_muted());
    }

    /// The alert hold unmutes the sink for a ringing alarm WITHOUT forgetting that the user asked
    /// for silence — otherwise the release would have nothing to restore.
    #[tokio::test]
    async fn alert_hold_does_not_touch_the_users_intent() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.alert_hold().await.unwrap();
        assert!(vol.user_muted(), "the hold must not clear the user's mute");
        let calls = log.lock().unwrap().clone();
        assert!(calls[0].contains("set-mute") && calls[0].ends_with('1'), "got {}", calls[0]);
        assert!(calls[1].contains("set-mute") && calls[1].ends_with('0'), "got {}", calls[1]);
    }

    /// The hub re-sends the hold at the top of every ring round, so a repeat must be free: the
    /// sink is already audible and a second unmute write would spam wpctl for the whole alarm.
    #[tokio::test]
    async fn repeated_alert_hold_writes_the_sink_only_once() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        log.lock().unwrap().clear();

        vol.alert_hold().await.unwrap();
        vol.alert_hold().await.unwrap();
        vol.alert_hold().await.unwrap();

        assert_eq!(log.lock().unwrap().clone(), vec!["wpctl set-mute SINK 0".to_string()]);
    }

    /// An unmute is what the user just asked for and cannot silence anything, so unlike a mute it
    /// is never deferred by an outstanding hold.
    #[tokio::test]
    async fn unmute_during_an_alert_hold_lands_immediately() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.alert_hold().await.unwrap();
        log.lock().unwrap().clear();

        vol.set_user_mute(false).await.unwrap();

        assert!(!vol.user_muted());
        assert_eq!(log.lock().unwrap().clone(), vec!["wpctl set-mute SINK 0".to_string()]);
    }

    /// A failed hold un-marks itself so the hub's next per-round re-assert retries it. Marked but
    /// un-unmuted would leave the alarm ringing into a muted sink for its whole duration.
    #[tokio::test]
    async fn failed_alert_hold_leaves_no_hold_outstanding() {
        let vol = VolumeControl::failing(10);
        vol.user_muted.store(true, Ordering::SeqCst);

        assert!(vol.alert_hold().await.is_err());
        assert!(!vol.alert_held(), "a hold whose unmute failed must be retryable");
    }

    /// The teardown path C2's reconnect case runs through: the hub dies while a mute is deferred,
    /// so the guard has to apply that mute rather than only ever un-muting.
    #[tokio::test]
    async fn release_hold_detached_applies_a_deferred_mute() {
        let (log, vol) = probe(10);
        vol.alert_hold().await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        log.lock().unwrap().clear();

        vol.release_hold_detached();

        assert!(!vol.alert_held());
        assert_eq!(log.lock().unwrap().clone(), vec!["wpctl set-mute SINK 1".to_string()]);
    }

    /// The same teardown on the ALSA backend. `release_hold_detached` is the one path that builds
    /// a command line outside `run()` — it cannot await the gate, so it spawns std::process
    /// directly — which is exactly why the amixer shape it fires is worth pinning too.
    #[tokio::test]
    async fn alsa_release_hold_detached_applies_a_deferred_mute() {
        let (log, vol) = alsa_probe(10);
        vol.alert_hold().await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        log.lock().unwrap().clear();

        vol.release_hold_detached();

        assert!(!vol.alert_held());
        assert_eq!(log.lock().unwrap().clone(), vec!["amixer -c hat sset Master mute".to_string()]);
    }

    /// With no hold outstanding there is nothing to restore, and writing anyway would let a
    /// teardown overwrite a sink state the satellite never changed.
    #[tokio::test]
    async fn release_hold_detached_without_a_hold_writes_nothing() {
        let (log, vol) = probe(10);
        vol.release_hold_detached();
        assert!(log.lock().unwrap().is_empty());
    }

    /// A failed set-mute must not leave the satellite believing a mute that never landed: the
    /// next alert-release would then mute a speaker the user never silenced.
    #[tokio::test]
    async fn failed_set_user_mute_rolls_the_tracked_state_back() {
        let vol = VolumeControl::failing(10);
        assert!(vol.set_user_mute(true).await.is_err());
        assert!(!vol.user_muted(), "a failed mute must not be remembered as muted");
    }

    #[tokio::test]
    async fn disabled_control_makes_every_action_a_no_op() {
        let vol = VolumeControl::new(None, None, None, 10);
        assert!(!vol.enabled());
        vol.step(true).await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        assert!(!vol.user_muted(), "a disabled control tracks nothing");
    }

    /// Reproduces the race a missing gate allows: `set_user_mute` stores its bookkeeping BEFORE
    /// awaiting its own `wpctl` call, so a second overlapping `set_user_mute` can overwrite that
    /// bookkeeping while the first call's subprocess is still in flight. The probe backend makes
    /// a "mute on" (`...1`) command land after a few more scheduler turns than a "mute off"
    /// (`...0`) one (see the `Probe` arm of `run`), which — on the single-threaded test
    /// runtime — deterministically inverts completion order relative to spawn/store order: the
    /// call that stores ITS bookkeeping first is the one whose `wpctl` call actually lands last.
    /// Without the gate that leaves `user_muted()` disagreeing with the last recorded call; with
    /// it, the two calls run fully one after another, so whichever finishes last decides both.
    #[tokio::test]
    async fn concurrent_set_user_mute_calls_serialize_and_stay_consistent() {
        let (log, vol) = probe(10);

        let mute_on = { let vol = vol.clone(); tokio::spawn(async move { vol.set_user_mute(true).await }) };
        let mute_off = { let vol = vol.clone(); tokio::spawn(async move { vol.set_user_mute(false).await }) };

        mute_on.await.unwrap().unwrap();
        mute_off.await.unwrap().unwrap();

        let calls = log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2, "got {calls:?}");
        let last_landed_muted = calls.last().unwrap().ends_with('1');
        assert_eq!(
            vol.user_muted(),
            last_landed_muted,
            "user_muted() must agree with whichever call's wpctl call actually landed last; \
             calls = {calls:?}, user_muted = {}",
            vol.user_muted()
        );
    }
}
