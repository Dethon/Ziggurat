//! The one seam in this crate.
//!
//! Everything the speech typist needs from outside itself — the keyboard, the microphone, the
//! window in front, the tray, the speaker, and Lemonade — sits behind [`Host`] and its inward
//! event channel. The core holds nothing else, which is why the whole of the interesting
//! behaviour is testable in WSL with no Windows, no microphone and no network, and why a Linux
//! implementation would be a second implementation of this trait and no other change.

use async_trait::async_trait;

/// A key as the platform names it. Opaque to the core: it compares them and nothing else, so a
/// virtual-key code, a keysym or an evdev code all fit without the core knowing which it holds.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, serde::Serialize, serde::Deserialize)]
pub struct KeyCode(pub u32);

/// How the words are made to arrive. A switch a person flips, never auto-detection: an
/// application deciding for itself which method it gets is a source of surprise, and the whole
/// point of the escape hatch is knowing when it is in use.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum InjectionMethod {
    /// Synthetic Unicode key events, batched into as few calls as possible.
    #[default]
    Keys,
    /// Set the clipboard, paste, restore the previous contents. For the applications that
    /// mishandle synthetic input.
    ClipboardPaste,
}

/// One transcript on its way into the window in front.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Injection<'a> {
    pub text: &'a str,
    /// The binding's key if it is still being held, because segments are typed while the person
    /// is still speaking. If that key is a modifier, the host must release it for the duration of
    /// the call and restore it afterwards, or every character arrives chorded. Which keys are
    /// modifiers is platform knowledge, so the core hands over the key rather than the answer.
    /// The shipped default binding has no modifier precisely so this is rarely exercised.
    pub held: Option<KeyCode>,
    pub method: InjectionMethod,
}

/// Which window is in front, as the platform identifies it. The core only ever compares two.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct WindowId(pub u64);

/// What the tray icon shows. Four states and no more: a person glancing at the tray wants to tell
/// a slow Whisper from a dead one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrayState {
    Idle,
    Recording,
    Transcribing,
    Error,
}

/// The one cue. It means "speak now", which is why it is played after the capture device is open
/// rather than when the key was received — that ordering is the whole mitigation for opening the
/// device on key-down with no pre-roll.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Cue {
    Start,
}

/// What the capture device turned out to be. There is no resampler: whatever rate the device
/// opened at is the rate the WAV declares, matching what the .NET side already does by taking
/// whatever rate it was handed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CaptureFormat {
    pub sample_rate: u32,
}

/// One request at Lemonade. The model is the client's, because it is configuration rather than
/// something the core decides per segment.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TranscriptionRequest {
    pub wav: Vec<u8>,
    pub language: String,
    pub prompt: Option<String>,
}

/// The words a segment turned out to hold, and the two quality signals the gate reads. A signal
/// that was absent or malformed is `None` — no signal, never a failure.
#[derive(Clone, Debug, PartialEq)]
pub struct Transcript {
    pub text: String,
    pub avg_logprob: Option<f64>,
    pub no_speech_prob: Option<f64>,
}

impl Transcript {
    pub fn words(text: impl Into<String>) -> Self {
        Self { text: text.into(), avg_logprob: None, no_speech_prob: None }
    }
}

/// Distinguishable because the retry path and the error notification want to say which happened.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TranscribeError {
    /// The request did not answer inside the per-segment timeout.
    Timeout,
    /// Lemonade answered, and said no.
    Status(u16),
    /// Nothing answered: connection refused, DNS, a socket that died mid-body.
    Transport(String),
    /// Something answered and it was not a transcription response.
    Malformed(String),
}

impl std::fmt::Display for TranscribeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Timeout => write!(f, "Lemonade did not answer in time"),
            Self::Status(code) => write!(f, "Lemonade answered {code}"),
            Self::Transport(why) => write!(f, "could not reach Lemonade: {why}"),
            Self::Malformed(why) => write!(f, "Lemonade sent something unreadable: {why}"),
        }
    }
}

impl std::error::Error for TranscribeError {}

/// Anything outward that can fail for a reason the core has to tell the person about.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HostError(pub String);

impl std::fmt::Display for HostError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for HostError {}

/// What arrives from outside. A real host fills the channel from the keyboard hook, the capture
/// device and a timer; a test fills it directly.
#[derive(Clone, Debug, PartialEq)]
pub enum HostEvent {
    /// A bound key went down. Keys that are not bound never reach here.
    BindingDown(KeyCode),
    BindingUp(KeyCode),
    /// Mono PCM at the rate [`CaptureFormat`] reported.
    Frame(Vec<i16>),
    /// A monotonic clock reading in milliseconds. The watchdog is the only thing that reads it,
    /// and it is an event rather than a `sleep` so that a test can move time without waiting.
    Tick { at_ms: u64 },
    /// Learn mode finished: the tray asked to rebind `binding` and this is the key that was
    /// pressed. Capturing it belongs to the host, because it is the keyboard hook reading one
    /// event rather than new machinery, and because no key but this one should reach the core.
    BindingLearned { binding: usize, key: KeyCode },
    /// The tray asked to quit.
    Quit,
}

/// The outward half of the seam. Every implementation of this is thin enough to read, which is
/// what makes "verified by hand on Windows" an acceptable answer for the real one.
#[async_trait]
pub trait Host: Send + Sync {
    /// Opens the capture device, answering the format it actually opened at. Called when a
    /// binding goes down; there is deliberately no pre-roll ring, so the first 50-200 ms of a
    /// dictation is traded for not holding the device open.
    fn open_capture(&self) -> Result<CaptureFormat, HostError>;

    /// Closes it. Called on key-up, on the watchdog, and on any path that abandons a dictation.
    fn close_capture(&self);

    /// Which window is in front right now.
    fn window_in_front(&self) -> WindowId;

    /// Types the text into whatever is in front, as though a person had typed it.
    fn inject(&self, injection: Injection<'_>) -> Result<(), HostError>;

    /// Which keys start a dictation, and are therefore swallowed for as long as they are held.
    /// The hook has to answer that synchronously in its callback, so the set lives on the host
    /// side; the core sends it at startup and again whenever learn mode changes it.
    fn set_bindings(&self, keys: &[KeyCode]);

    fn set_tray(&self, state: TrayState);

    fn play_cue(&self, cue: Cue);

    /// Tells the person something once. The core is careful about how often it calls this: with
    /// Lemonade down every segment fails, and one notification per segment would be a stream of
    /// them while the person is still speaking.
    fn notify(&self, message: &str);

    async fn transcribe(&self, request: TranscriptionRequest) -> Result<Transcript, TranscribeError>;
}
