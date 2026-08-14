//! What the speech typist reads out of its `config.toml`.
//!
//! Every threshold and window the core uses is a key here rather than a constant, because they
//! exist because of what a particular room and a particular microphone sound like.

use serde::{Deserialize, Serialize};

use crate::host::KeyCode;

mod file;

pub use file::{load, locations, ConfigError, Loaded, DEFAULTS, FILE_NAME};

/// F13. No application uses it, which is what makes it a safe default — and many keyboards cannot
/// produce it, which is what the tray's learn mode is for.
pub const DEFAULT_BINDING_KEY: KeyCode = KeyCode(0x7C);

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct Config {
    pub lemonade: LemonadeConfig,
    pub audio: AudioConfig,
    pub detector: DetectorConfig,
    pub gate: GateConfig,
    pub injection: InjectionConfig,
    pub bindings: Vec<Binding>,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            lemonade: LemonadeConfig::default(),
            audio: AudioConfig::default(),
            detector: DetectorConfig::default(),
            gate: GateConfig::default(),
            injection: InjectionConfig::default(),
            bindings: vec![Binding::default()],
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct LemonadeConfig {
    /// Names the Lemonade host, not the compose-internal `lemonade:13305`, which means nothing
    /// from a Windows desktop.
    pub base_url: String,
    /// Must agree with `STT_MODEL` by hand: compose's `&stt-model` anchor cannot reach this file.
    /// A mismatch is not an error — Lemonade lazily pulls whatever it was asked for — so the
    /// symptom is a slow first dictation rather than a failure.
    pub model: String,
    pub request_timeout_secs: u64,
}

impl Default for LemonadeConfig {
    fn default() -> Self {
        Self {
            base_url: "http://ai370:13305/v1".into(),
            model: "Whisper-Large-v3-Turbo".into(),
            request_timeout_secs: 30,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct AudioConfig {
    /// A case-insensitive fragment of the capture device's name. Empty means the system default,
    /// so one microphone needs no configuration at all.
    pub device_name: String,
    pub cues: CueConfig,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct CueConfig {
    pub enabled: bool,
}

impl Default for CueConfig {
    fn default() -> Self {
        Self { enabled: true }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct DetectorConfig {
    /// Frame energy, in i16 RMS amplitude, at or above which audio counts as speech.
    pub speech_rms: f32,
    /// The hysteresis: once speaking, it takes this much quieter to stop counting as speech.
    pub silence_rms: f32,
    /// How long below the silence threshold cuts a segment.
    pub silence_cut_ms: u32,
    /// Kept either side of a cut so a leading plosive is not clipped off the next segment.
    pub padding_ms: u32,
    /// A monologue with no pause is force-cut at this age, keeping every segment inside Whisper's
    /// 30 s window.
    pub max_segment_ms: u32,
    /// The force cut lands at the lowest-energy point in this much of the preceding audio, so the
    /// split falls in a gap between words rather than mid-syllable.
    pub force_cut_search_ms: u32,
}

impl Default for DetectorConfig {
    fn default() -> Self {
        Self {
            speech_rms: 500.0,
            silence_rms: 300.0,
            silence_cut_ms: 400,
            padding_ms: 200,
            max_segment_ms: 20_000,
            force_cut_search_ms: 1_000,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct GateConfig {
    /// Above this, the transcript is whisper hallucinating on near-silence and is dropped.
    pub max_no_speech_prob: f64,
    /// Below this, whisper was guessing, and the guess is dropped.
    pub min_avg_logprob: f64,
}

impl Default for GateConfig {
    fn default() -> Self {
        Self { max_no_speech_prob: 0.6, min_avg_logprob: -1.0 }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum InjectionMethod {
    /// Synthetic Unicode key events, batched into as few calls as possible.
    #[default]
    Keys,
    /// Set the clipboard, paste, restore the previous contents. For the applications that
    /// mishandle synthetic input. Never chosen automatically.
    ClipboardPaste,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct InjectionConfig {
    pub method: InjectionMethod,
    /// A dictation whose key-up never arrived — remote desktop, fast user switching, a hook that
    /// lost its window — ends by itself after this long. Without it a lost key-up holds the
    /// microphone for as long as the process lives.
    pub watchdog_secs: u64,
}

impl Default for InjectionConfig {
    fn default() -> Self {
        Self { method: InjectionMethod::default(), watchdog_secs: 120 }
    }
}

/// A key together with the language its transcripts are expected in and the vocabulary they
/// should be spelled by. Several exist at once and are all live; there is no active binding and
/// no mode.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, default)]
pub struct Binding {
    pub key: KeyCode,
    /// Matches the `"Language": "es"` every other STT caller in this repo pins.
    pub language: String,
    /// Names, jargon and project terms, spelled the way they should come back.
    pub vocabulary: String,
}

impl Default for Binding {
    fn default() -> Self {
        Self { key: DEFAULT_BINDING_KEY, language: "es".into(), vocabulary: String::new() }
    }
}

impl Config {
    /// Which binding a key belongs to, or `None` for a key that is not bound at all.
    pub fn binding_for(&self, key: KeyCode) -> Option<usize> {
        self.bindings.iter().position(|b| b.key == key)
    }

    /// What TOML cannot express: a config that parses and still leaves the speech typist unable
    /// to do anything is reported rather than started.
    pub fn validate(&self) -> Result<(), String> {
        if self.bindings.is_empty() {
            return Err("no bindings, so no key would ever start a dictation".into());
        }
        let mut keys: Vec<KeyCode> = self.bindings.iter().map(|b| b.key).collect();
        keys.sort();
        let clash = keys.windows(2).find(|pair| pair[0] == pair[1]);
        if let Some(pair) = clash {
            return Err(format!(
                "key {} is bound twice, so neither binding's language could win",
                pair[0].0
            ));
        }
        Ok(())
    }
}
