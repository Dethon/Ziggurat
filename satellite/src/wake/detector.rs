use std::collections::VecDeque;
use std::time::{Duration, Instant};
use tract_onnx::prelude::*;

// tract 0.23: into_runnable() yields Arc<RunnableModel<F, O>>; TypedRunnableModel is the
// prelude alias for RunnableModel<TypedFact, Box<dyn TypedOp>> — run() takes &Arc<Self>.
type Model = Arc<TypedRunnableModel>;

const MEL_MODEL: &[u8] = include_bytes!("../../models/melspectrogram.onnx");
const EMB_MODEL: &[u8] = include_bytes!("../../models/embedding_model.onnx");
const CLF_MODEL: &[u8] = include_bytes!("../../models/ok_nabu.onnx");

const CHUNK: usize = 1280;    // 80 ms @ 16 kHz
const LOOKBACK: usize = 480;  // 160*3 samples of mel context carried across chunks (openwakeword)
const MEL_FRAMES: usize = 76; // embedding window: the last 76 mel frames
const EMB_DIM: usize = 96;
const CLF_FRAMES: usize = 16; // classifier window: the last 16 embeddings

/// Root-mean-square level of a chunk of i16 PCM samples, in i16-amplitude units (0..=32768).
/// Diagnostic only: lets the wake path log how loud each chunk is (silence vs speech vs music)
/// so detector tuning (threshold, window, gain) is driven by real on-device levels.
pub fn chunk_rms(samples: &[i16]) -> f32 {
    if samples.is_empty() { return 0.0; }
    let sum_sq: f64 = samples.iter().map(|&s| s as f64 * s as f64).sum();
    (sum_sq / samples.len() as f64).sqrt() as f32
}

#[derive(Clone)]
pub struct DetectorConfig {
    pub threshold: f32,
    /// How many consecutive classifier scores are averaged before the mean is compared against
    /// `threshold`. 1 compares each score directly. Raising it buys noise immunity at one extra
    /// 80 ms frame of wake latency per step — past 3 the total exceeds `wake_preroll_ms`'s 240 ms
    /// gap flush, so bump that too.
    pub window: u32,
    pub refractory: Duration,
    /// Room level under which `quiet_threshold` / `quiet_window` replace `threshold` / `window`
    /// (see `Ambient` for the statistic). 0 = never: one rule at every level.
    ///
    /// It exists because the classifier's false fires and its real ones only differ in shape:
    /// a false fire is a one-frame blip (0.07 0.66 0.79 0.44 on the deployed XVF3800 unit, out
    /// of a room at RMS 25), a real wake holds ~0.98 for four frames. A window mean lenient
    /// enough to ride through the jittery trace TV or music gives a real wake also fires on the
    /// blip. In a quiet room nothing jitters a real wake, so it can be held to the strict shape
    /// there for free, while a room with something playing keeps the lenient rule it needs.
    /// Measured on 77.5 h of that unit's mic (2026-10-10..13): 0.9 over 2 frames below 200 kept
    /// 16 of 16 real wakes — 15 on the very frame the lenient rule fired — and dropped 3 of 4
    /// false ones. 200 is that mic's number, as `--wake-min-rms`'s 350 is.
    pub quiet_below_rms: f32,
    pub quiet_threshold: f32,
    pub quiet_window: u32,
}
impl Default for DetectorConfig {
    fn default() -> Self {
        Self {
            threshold: 0.5,
            window: 1,
            refractory: Duration::from_secs_f32(2.0),
            quiet_below_rms: 0.0,
            quiet_threshold: 0.9,
            quiet_window: 2,
        }
    }
}

/// One operating point of the wake decision: fire when the mean of the last `window` scores
/// reaches `threshold`.
#[derive(Clone, Copy)]
struct Rule {
    threshold: f32,
    window: usize,
}

const AMBIENT_CHUNKS: usize = 375;    // 30 s of 80 ms chunks
const AMBIENT_MIN_CHUNKS: usize = 63; // ~5 s heard before the reading means anything

/// Whether something is playing in the room: the MEDIAN chunk RMS over the last 30 s the
/// detector heard.
///
/// Deliberately not `audio::room::RoomLevel`. That one answers "what does silence sound like
/// here" for the hub's end-of-utterance gate, so it is a minimum and reads through to the room
/// behind whatever is playing. This one must do the opposite: TV dialog with its lulls, or
/// music with its gaps, has to read as a loud room, and a second of somebody speaking the wake
/// word into a silent one has to read as quiet. The median of half a minute does both.
struct Ambient {
    levels: VecDeque<f32>,
}

impl Ambient {
    fn new() -> Self { Self { levels: VecDeque::with_capacity(AMBIENT_CHUNKS + 1) } }

    fn push(&mut self, rms: f32) {
        self.levels.push_back(rms);
        while self.levels.len() > AMBIENT_CHUNKS { self.levels.pop_front(); }
    }

    /// `None` until ~5 s have been heard: a detector that cannot tell yet keeps the lenient rule.
    fn level(&self) -> Option<f32> {
        if self.levels.len() < AMBIENT_MIN_CHUNKS { return None; }
        let mut sorted: Vec<f32> = self.levels.iter().copied().collect();
        let mid = sorted.len() / 2;
        Some(*sorted.select_nth_unstable_by(mid, f32::total_cmp).1)
    }
}

/// The wake decision rule: a sliding mean of the last `window` classifier scores, fired when
/// that mean reaches `threshold`.
///
/// This replaced a consecutive-frame counter that reset to zero on any single frame below
/// threshold. With a jittery score trace — which is what background TV or music produces — one
/// dip between two strong frames threw the whole utterance away, so the satellite would not
/// wake even when the phrase was clearly spoken. Averaging rides through the dip. It is also
/// what microWakeWord/ESPHome does, and it subsumes the old `trigger_level` knob: `window` 1 is
/// the old level 1, and `window` n is strictly more permissive than the old level n (that
/// required every frame at or above threshold; the mean only needs them to average it).
///
///
/// It holds two operating points and is told on every frame which one the room calls for (see
/// `DetectorConfig::quiet_below_rms`). Both read the same score history, so a room that crosses
/// the line mid-utterance is judged on the frames it actually produced.
///
/// Deliberately model-free so the rule is testable with plain numbers, no ONNX inference.
struct WindowTrigger {
    scores: VecDeque<f32>,
    rule: Rule,
    quiet_rule: Rule,
    refractory: Duration,
    refractory_until: Option<Instant>,
}

impl WindowTrigger {
    fn new(cfg: &DetectorConfig) -> Self {
        let rule = Rule { threshold: cfg.threshold, window: (cfg.window as usize).max(1) };
        let quiet_rule =
            Rule { threshold: cfg.quiet_threshold, window: (cfg.quiet_window as usize).max(1) };
        Self {
            scores: VecDeque::with_capacity(rule.window.max(quiet_rule.window)),
            rule,
            quiet_rule,
            refractory: cfg.refractory,
            refractory_until: None,
        }
    }

    /// Feed one classifier score, judged by the quiet-room rule when `quiet`. Returns
    /// `(fired, mean)`, where `mean` is over the scores that rule looks at — diagnostic only,
    /// and 0.0 while the refractory is suppressing input.
    ///
    /// A partial window never fires: after a fire or a reset the next `window - 1` frames carry
    /// no decision. Firing clears the buffer, so the stale high scores that produced it cannot
    /// immediately re-fire once the refractory expires.
    fn push(&mut self, score: f32, quiet: bool) -> (bool, f32) {
        if let Some(until) = self.refractory_until {
            if Instant::now() < until { return (false, 0.0); }
            self.refractory_until = None;
        }
        let rule = if quiet { self.quiet_rule } else { self.rule };
        self.scores.push_back(score);
        while self.scores.len() > self.rule.window.max(self.quiet_rule.window) { self.scores.pop_front(); }
        // The newest `rule.window` scores; the buffer is as long as the longer of the two rules.
        let n = self.scores.len().min(rule.window);
        let mean = self.scores.iter().skip(self.scores.len() - n).sum::<f32>() / n as f32;
        if n < rule.window { return (false, mean); }
        if mean >= rule.threshold {
            self.scores.clear();
            self.refractory_until = Some(Instant::now() + self.refractory);
            return (true, mean);
        }
        (false, mean)
    }

    fn reset(&mut self) {
        self.scores.clear();
        self.refractory_until = None;
    }
}

/// The three openwakeword ONNX models, parsed + graph-optimized ONCE per process: optimization
/// costs real time on an A53, and the hub reconnects forever — per-connection detectors built
/// from a loaded bundle only clone Arcs and seed buffers, so re-arm after a reconnect is instant.
#[derive(Clone)]
pub struct WakeModels {
    mel: Model,
    emb: Model,
    clf: Model,
}

impl WakeModels {
    pub fn load() -> anyhow::Result<Self> { Self::load_with_classifier(CLF_MODEL) }

    /// Classifier bytes are a parameter so tests can pair the stock fixture wav with the
    /// stock classifier it was validated against, independent of whichever classifier
    /// (custom-trained or otherwise) `load()` ships in production.
    pub fn load_with_classifier(classifier: &[u8]) -> anyhow::Result<Self> {
        let load = |b: &[u8], shape: &[usize]| -> anyhow::Result<Model> {
            tract_onnx::onnx()
                .model_for_read(&mut std::io::Cursor::new(b))?
                .with_input_fact(0, f32::fact(shape).into())?
                .into_optimized()?
                .into_runnable()
        };
        Ok(Self {
            mel: load(MEL_MODEL, &[1, LOOKBACK + CHUNK])?,
            emb: load(EMB_MODEL, &[1, MEL_FRAMES, 32, 1])?,
            clf: load(classifier, &[1, CLF_FRAMES, EMB_DIM])?,
        })
    }
}

pub struct WakeDetector {
    // NOTE: these stay Arc<TypedRunnableModel> and pay Model::run()'s per-call SimpleState
    // spawn. Holding persistent SimpleStates would save that allocation but SimpleState is
    // !Send (Box<dyn OpState> has no Send bound), and the detector lives across awaits in a
    // tokio::spawn'd connection task.
    mel: Model,
    emb: Model,
    clf: Model,
    tail: Vec<f32>,                    // last LOOKBACK samples, zero-seeded
    mel_buf: VecDeque<[f32; 32]>,      // last 76 mel frames, ones-seeded (mirrors openwakeword)
    emb_buf: VecDeque<[f32; EMB_DIM]>, // last 16 embeddings
    trigger: WindowTrigger,
    ambient: Ambient,
    quiet_below_rms: f32,
}

impl WakeDetector {
    pub fn new(models: &WakeModels, cfg: DetectorConfig) -> anyhow::Result<Self> {
        Ok(Self {
            mel: models.mel.clone(),
            emb: models.emb.clone(),
            clf: models.clf.clone(),
            tail: vec![0f32; LOOKBACK],
            mel_buf: (0..MEL_FRAMES).map(|_| [1f32; 32]).collect(),
            emb_buf: VecDeque::new(),
            trigger: WindowTrigger::new(&cfg),
            ambient: Ambient::new(),
            quiet_below_rms: cfg.quiet_below_rms,
        })
    }

    /// Feed exactly 1280 samples (80 ms). Returns the classifier score when a wake fires.
    /// The streaming algorithm mirrors openwakeword's AudioFeatures (validated to 4 decimals
    /// against the Python package): mel over lookback+chunk, ones-seeded mel buffer, one
    /// embedding per chunk from the last 76 frames, classify the last 16 embeddings.
    pub fn push_chunk(&mut self, chunk: &[i16]) -> Option<f32> {
        assert_eq!(chunk.len(), CHUNK, "push_chunk requires exactly {CHUNK} samples");
        let rms = chunk_rms(chunk);
        self.ambient.push(rms);
        // Stage 1: mel over lookback + chunk -> 8 new frames (x/10 + 2)
        let mut input = vec![0f32; LOOKBACK + CHUNK];
        input[..LOOKBACK].copy_from_slice(&self.tail);
        for (i, s) in chunk.iter().enumerate() { input[LOOKBACK + i] = *s as f32; }
        self.tail.copy_from_slice(&input[CHUNK..]); // keep the last 480 samples for the next chunk
        let t: Tensor =
            tract_ndarray::Array2::from_shape_vec((1, LOOKBACK + CHUNK), input).unwrap().into();
        let out = self.mel.run(tvec!(t.into())).expect("mel run");
        let flat: Vec<f32> = out[0].to_plain_array_view::<f32>().unwrap().iter().map(|v| v / 10.0 + 2.0).collect();
        for frame in flat.chunks_exact(32) {
            let mut f = [0f32; 32];
            f.copy_from_slice(frame);
            self.mel_buf.push_back(f);
            while self.mel_buf.len() > MEL_FRAMES { self.mel_buf.pop_front(); }
        }
        // Stage 2: ONE embedding from the last 76 mel frames (implicit 8-frame / 80 ms hop)
        let mut w = vec![0f32; MEL_FRAMES * 32];
        for (r, frame) in self.mel_buf.iter().enumerate() {
            w[r * 32..(r + 1) * 32].copy_from_slice(frame);
        }
        let t: Tensor =
            tract_ndarray::Array4::from_shape_vec((1, MEL_FRAMES, 32, 1), w).unwrap().into();
        let eo = self.emb.run(tvec!(t.into())).expect("emb run");
        let ev = eo[0].to_plain_array_view::<f32>().unwrap();
        let mut e = [0f32; EMB_DIM];
        for (i, v) in ev.iter().take(EMB_DIM).enumerate() { e[i] = *v; }
        self.emb_buf.push_back(e);
        if self.emb_buf.len() > CLF_FRAMES { self.emb_buf.pop_front(); }
        // Stage 3: classify the last 16 embeddings
        if self.emb_buf.len() == CLF_FRAMES {
            let mut c = vec![0f32; CLF_FRAMES * EMB_DIM];
            for (i, em) in self.emb_buf.iter().enumerate() {
                c[i * EMB_DIM..(i + 1) * EMB_DIM].copy_from_slice(em);
            }
            let ct: Tensor =
                tract_ndarray::Array3::from_shape_vec((1, CLF_FRAMES, EMB_DIM), c).unwrap().into();
            let co = self.clf.run(tvec!(ct.into())).expect("clf run");
            let score = co[0].to_plain_array_view::<f32>().unwrap()[[0, 0]];
            // 0 switches the quiet-room rule off, and then the median is never taken.
            let ambient = if self.quiet_below_rms > 0.0 { self.ambient.level() } else { None };
            let quiet = ambient.is_some_and(|level| level < self.quiet_below_rms);
            // The trigger must see EVERY frame, so push before the log gate below.
            let (fired, mean) = self.trigger.push(score, quiet);
            // Diagnostic (tuning): per-chunk wake score, the window mean the rule actually acts
            // on, which rule that was and the room level that chose it, and mic level — for
            // setting threshold / window from real on-device data. At debug so it's silent in
            // production (RUST_LOG=info) but available via RUST_LOG=...=debug when tuning; gated
            // at a low score floor so even at debug idle silence doesn't flood.
            if score >= 0.05 { tracing::debug!(score, mean, quiet, ambient, rms, "wake score"); }
            if fired { return Some(score); }
        }
        None
    }

    /// Clear streaming state when re-arming after a turn. The ambient level is kept: the capture
    /// itself never reaches the detector, so what it holds is still the room before the turn,
    /// and dropping it would hand the ~5 s after every turn to the lenient rule.
    pub fn reset(&mut self) {
        self.tail.fill(0.0);
        self.mel_buf = (0..MEL_FRAMES).map(|_| [1f32; 32]).collect();
        self.emb_buf.clear();
        self.trigger.reset();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    // The shipped models/ok_nabu.onnx is a custom-trained classifier; tests/fixtures/ok_nabu.wav
    // is the stock recording validated against the stock classifier (77db1daa). Detector
    // plumbing — refractory, shared bundle, score surfacing — is what these tests cover, so
    // they pair the stock model with the stock wav.
    const STOCK_CLF_MODEL: &[u8] = include_bytes!("../../tests/fixtures/ok_nabu_stock.onnx");
    fn wav(path: &str) -> Vec<i16> {
        let mut r = hound::WavReader::open(path).unwrap();
        r.samples::<i16>().map(|s| s.unwrap()).collect()
    }
    #[test]
    fn fires_once_on_ok_nabu_then_respects_refractory() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/ok_nabu.wav").chunks_exact(1280) {
            if d.push_chunk(chunk).is_some() { fires += 1; }
        }
        assert_eq!(fires, 1, "exactly one wake from one utterance");
    }
    #[test]
    fn silent_on_silence() {
        let models = WakeModels::load().unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/silence.wav").chunks_exact(1280) {
            if d.push_chunk(chunk).is_some() { fires += 1; }
        }
        assert_eq!(fires, 0);
    }
    // Reconnect path: detectors built from ONE loaded bundle must detect independently —
    // shared optimized models, per-detector streaming state.
    #[test]
    fn detectors_share_one_model_bundle() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let samples = wav("tests/fixtures/ok_nabu.wav");
        let fires = |d: &mut WakeDetector| samples.chunks_exact(1280).filter(|c| d.push_chunk(c).is_some()).count();
        let mut first = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut second = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        assert_eq!(fires(&mut first), 1);
        assert_eq!(fires(&mut second), 1, "a fresh detector from the shared bundle must behave identically");
    }

    #[test]
    fn chunk_rms_matches_known_values() {
        assert_eq!(chunk_rms(&[]), 0.0);
        assert_eq!(chunk_rms(&[100, -100, 100, -100]), 100.0); // constant magnitude -> rms == magnitude
        let r = chunk_rms(&[3, 4]);
        assert!((r - 12.5f32.sqrt()).abs() < 1e-3, "rms([3,4]) = sqrt((9+16)/2) = sqrt(12.5), got {r}");
    }

    #[test]
    fn push_chunk_reports_score_on_wake() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let scores: Vec<f32> = wav("tests/fixtures/ok_nabu.wav")
            .chunks_exact(1280)
            .filter_map(|c| d.push_chunk(c))
            .collect();
        assert_eq!(scores.len(), 1, "exactly one wake from one utterance");
        assert!(scores[0] >= 0.5, "winning score is at least the threshold, got {}", scores[0]);
    }

    // ---- WindowTrigger: the decision rule, tested with plain numbers (no ONNX inference) ----

    fn trigger(window: u32, threshold: f32, refractory_ms: u64) -> WindowTrigger {
        WindowTrigger::new(&DetectorConfig {
            threshold,
            window,
            refractory: Duration::from_millis(refractory_ms),
            ..DetectorConfig::default()
        })
    }

    /// The whole point of the change: the old consecutive-frame counter reset on any single
    /// frame below threshold, so a jittery score trace in noise threw the utterance away.
    #[test]
    fn window_mean_fires_through_a_single_frame_dip() {
        let mut t = trigger(3, 0.7, 0);
        assert!(!t.push(0.75, false).0);
        assert!(!t.push(0.68, false).0, "second frame dips below threshold");
        assert!(t.push(0.78, false).0, "mean 0.7367 clears 0.7 despite the dip");
    }

    #[test]
    fn window_mean_stays_silent_on_a_plateau_below_threshold() {
        let mut t = trigger(3, 0.7, 0);
        for s in [0.60, 0.62, 0.64, 0.63, 0.61] {
            assert!(!t.push(s, false).0, "mean never reaches 0.7, so {s} must not fire");
        }
    }

    /// A partial window must never fire: after a reset the first window-1 frames carry no
    /// decision, exactly as trigger_level 2 cost one frame of deadness before.
    #[test]
    fn partial_window_never_fires() {
        let mut t = trigger(3, 0.7, 0);
        assert!(!t.push(0.99, false).0);
        assert!(!t.push(0.99, false).0);
        assert!(t.push(0.99, false).0, "fires only once the window is full");
    }

    /// Firing clears the buffer. Without this the stale high scores still in it would re-fire
    /// on the very next frame once the refractory expired.
    #[test]
    fn firing_clears_the_window() {
        let mut t = trigger(3, 0.7, 0);
        let fires = (0..6).filter(|_| t.push(0.8, false).0).count();
        assert_eq!(fires, 2, "6 frames at window 3 = 2 fires, not 4");
    }

    #[test]
    fn refractory_suppresses_then_expires() {
        let mut t = trigger(1, 0.7, 40);
        assert!(t.push(0.9, false).0, "first frame fires");
        assert!(!t.push(0.9, false).0, "second is inside the refractory");
        std::thread::sleep(Duration::from_millis(60));
        assert!(t.push(0.9, false).0, "fires again once the refractory has expired");
    }

    /// window 1 is the default and must behave exactly like the old trigger_level 1: the
    /// instantaneous score compared straight against the threshold.
    #[test]
    fn window_of_one_fires_on_any_frame_at_threshold() {
        let mut t = trigger(1, 0.7, 0);
        assert!(t.push(0.8, false).0);
        assert!(!t.push(0.6, false).0);
        assert!(t.push(0.71, false).0);
    }

    #[test]
    fn reset_clears_window_and_refractory() {
        let mut t = trigger(3, 0.7, 60_000);
        assert!(!t.push(0.8, false).0);
        assert!(!t.push(0.8, false).0);
        t.reset();
        assert!(!t.push(0.8, false).0, "reset dropped the two buffered frames");
        assert!(!t.push(0.8, false).0);
        assert!(t.push(0.8, false).0, "a fresh full window fires");
        t.reset();
        assert!(!t.push(0.8, false).0, "reset also cleared the 60 s refractory");
        assert!(!t.push(0.8, false).0);
        assert!(t.push(0.8, false).0, "so a full window fires instead of being suppressed");
    }

    #[test]
    fn push_reports_the_mean_it_evaluated() {
        let mut t = trigger(2, 0.9, 0);
        assert_eq!(t.push(0.4, false).1, 0.4, "partial window reports what it has");
        let (fired, mean) = t.push(0.6, false);
        assert!(!fired);
        assert!((mean - 0.5).abs() < 1e-6, "mean of 0.4 and 0.6, got {mean}");
    }

    // ---- the quiet-room rule ----

    /// The deployed pairing: 0.5 over 3 frames with something playing, 0.9 over 2 in a quiet room.
    fn two_rule_trigger() -> WindowTrigger {
        WindowTrigger::new(&DetectorConfig {
            threshold: 0.5,
            window: 3,
            refractory: Duration::ZERO,
            quiet_below_rms: 200.0,
            quiet_threshold: 0.9,
            quiet_window: 2,
        })
    }

    fn fires(t: &mut WindowTrigger, trace: &[f32], quiet: bool) -> usize {
        trace.iter().filter(|&&s| t.push(s, quiet).0).count()
    }

    // Score traces lifted from the office unit's mic capture (2026-10-10..13).
    const BLIP: [f32; 7] = [0.00, 0.07, 0.66, 0.79, 0.44, 0.07, 0.00]; // room at RMS 25, nobody spoke
    const LOUD_BLIP: [f32; 7] = [0.00, 0.01, 0.66, 0.95, 0.58, 0.06, 0.00];
    const REAL_WAKE: [f32; 7] = [0.00, 0.04, 0.98, 0.99, 0.99, 0.99, 0.91];

    /// Why the rule exists: the lenient mean fires on a single high frame with two mediocre
    /// neighbours, which is all a false fire is.
    #[test]
    fn one_frame_blip_fires_the_lenient_rule_but_not_the_quiet_one() {
        for blip in [BLIP, LOUD_BLIP] {
            assert_eq!(fires(&mut two_rule_trigger(), &blip, false), 1, "lenient rule on {blip:?}");
            assert_eq!(fires(&mut two_rule_trigger(), &blip, true), 0, "quiet rule on {blip:?}");
        }
    }

    /// And why it is free in a quiet room: a real wake has two frames at 0.98 before the lenient
    /// rule's third frame has even arrived, so the quiet rule fires on the same frame.
    #[test]
    fn real_wake_fires_both_rules_on_the_same_frame() {
        let first_fire = |quiet| {
            let mut t = two_rule_trigger();
            REAL_WAKE.iter().position(|&s| t.push(s, quiet).0)
        };
        assert_eq!(first_fire(false), Some(3));
        assert_eq!(first_fire(true), Some(3));
    }

    /// The two rules share one history. Were each to keep its own, a room crossing the line
    /// mid-utterance would restart the window and lose the wake.
    #[test]
    fn rule_switch_mid_utterance_keeps_the_frames_already_heard() {
        let mut t = two_rule_trigger();
        assert!(!t.push(0.95, true).0, "one frame is not the quiet rule's two");
        assert!(!t.push(0.20, false).0, "lenient rule: only two of its three frames so far");
        assert!(t.push(0.50, false).0, "mean of 0.95 0.20 0.50 reaches 0.5 across the switch");
    }

    /// The quiet rule looks at its own, shorter window — not at whatever the longer rule buffered.
    #[test]
    fn quiet_rule_averages_only_its_own_window() {
        let mut t = two_rule_trigger();
        assert!(!t.push(0.10, true).0);
        assert!(!t.push(0.92, true).0);
        let (fired, mean) = t.push(0.94, true);
        assert!(fired, "0.92 and 0.94 average 0.93; the 0.10 before them is not in the window");
        assert!((mean - 0.93).abs() < 1e-6, "got {mean}");
    }

    fn ambient_of(levels: &[(f32, usize)]) -> Ambient {
        let mut a = Ambient::new();
        for &(level, chunks) in levels {
            for _ in 0..chunks { a.push(level); }
        }
        a
    }

    #[test]
    fn ambient_has_no_reading_until_five_seconds_are_heard() {
        assert_eq!(ambient_of(&[(30.0, AMBIENT_MIN_CHUNKS - 1)]).level(), None);
        assert_eq!(ambient_of(&[(30.0, AMBIENT_MIN_CHUNKS)]).level(), Some(30.0));
    }

    /// The wake word is the loudest second in the window and must not make its own room loud.
    #[test]
    fn ambient_reads_a_quiet_room_through_the_wake_word() {
        let a = ambient_of(&[(30.0, 300), (4000.0, 13)]);
        assert_eq!(a.level(), Some(30.0));
    }

    /// The opposite of `RoomLevel`, which reports the room behind the TV: here dialog with
    /// lulls between phrases has to read as a room with something playing.
    #[test]
    fn ambient_reads_tv_dialog_as_loud_despite_the_lulls() {
        let mut a = Ambient::new();
        for _ in 0..40 {
            for _ in 0..6 { a.push(900.0); } // phrase
            for _ in 0..3 { a.push(30.0); }  // 240 ms lull
        }
        assert_eq!(a.level(), Some(900.0));
    }

    #[test]
    fn ambient_forgets_what_is_older_than_the_window() {
        let a = ambient_of(&[(900.0, 400), (30.0, AMBIENT_CHUNKS)]);
        assert_eq!(a.level(), Some(30.0), "30 s of quiet have pushed the TV out");
    }

    // Detector-level: the room really does select the rule. A quiet rule nothing can satisfy
    // (threshold above 1) makes the selection visible as fire / no fire on the same utterance.
    fn fires_after_lead_in(quiet_below_rms: f32, lead_in_level: i16) -> usize {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let cfg = DetectorConfig { quiet_below_rms, quiet_threshold: 1.5, ..DetectorConfig::default() };
        let mut d = WakeDetector::new(&models, cfg).unwrap();
        // 8 s of room before the utterance: alternating sign, so the level is the amplitude.
        let room: Vec<i16> = (0..CHUNK).map(|i| if i % 2 == 0 { lead_in_level } else { -lead_in_level }).collect();
        let mut fires = (0..100).filter(|_| d.push_chunk(&room).is_some()).count();
        fires += wav("tests/fixtures/ok_nabu.wav").chunks_exact(CHUNK).filter(|c| d.push_chunk(c).is_some()).count();
        fires
    }

    #[test]
    fn quiet_room_puts_the_utterance_under_the_quiet_rule() {
        assert_eq!(fires_after_lead_in(200.0, 30), 0, "room at 30 is under 200: the unsatisfiable rule applies");
    }

    #[test]
    fn loud_room_keeps_the_lenient_rule() {
        assert_eq!(fires_after_lead_in(200.0, 900), 1, "room at 900 is over 200");
    }

    #[test]
    fn quiet_rule_is_off_at_zero() {
        assert_eq!(fires_after_lead_in(0.0, 30), 1, "0 = one rule at every level");
    }

    /// reset() re-arms after a turn; the room did not change because a turn happened.
    #[test]
    fn reset_keeps_the_ambient_level() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        for _ in 0..AMBIENT_MIN_CHUNKS { d.push_chunk(&[40i16; CHUNK]); }
        d.reset();
        assert_eq!(d.ambient.level(), Some(40.0));
    }
}
