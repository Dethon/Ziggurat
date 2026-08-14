//! The one fake host every core test drives.
//!
//! It records what it was asked to do, in order, and answers transcription from a script the test
//! wrote. Tests assert on what the speech typist did — which transcripts were injected, in what
//! order, with what joining; which were dropped; what the tray was asked to show; what was
//! actually sent to Lemonade — and never on how it got there.

use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use tokio::sync::Notify;

use crate::host::{
    CaptureFormat, Cue, Host, HostError, Injection, InjectionMethod, KeyCode, TranscribeError,
    Transcript, TranscriptionRequest, TrayState, WindowId,
};

#[derive(Clone, Debug, PartialEq)]
pub enum Action {
    CaptureOpened,
    CaptureClosed,
    Cue(Cue),
    Tray(TrayState),
    Bindings(Vec<KeyCode>),
    Sent(TranscriptionRequest),
    Injected { text: String, held: Option<KeyCode>, method: InjectionMethod },
    Notified(String),
}

#[derive(Default)]
struct State {
    actions: Vec<Action>,
    answers: std::collections::VecDeque<Result<Transcript, TranscribeError>>,
    foreground: u64,
    open_failure: Option<String>,
    inject_failure: Option<String>,
    sample_rate: u32,
}

pub struct FakeHost {
    state: Mutex<State>,
    changed: Notify,
}

impl FakeHost {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::new(State { foreground: 1, sample_rate: 16_000, ..State::default() }),
            changed: Notify::new(),
        })
    }

    /// Queues what the next transcription answers with. Answers are handed out in order, and a
    /// request with nothing left queued gets an empty transcript.
    pub fn will_answer(&self, answer: Result<Transcript, TranscribeError>) -> &Self {
        self.state.lock().unwrap().answers.push_back(answer);
        self
    }

    pub fn will_say(&self, text: &str) -> &Self {
        self.will_answer(Ok(Transcript::words(text)))
    }

    pub fn with_sample_rate(&self, rate: u32) -> &Self {
        self.state.lock().unwrap().sample_rate = rate;
        self
    }

    /// Moves the window in front, as clicking into another application would.
    pub fn move_to_window(&self, id: u64) {
        self.state.lock().unwrap().foreground = id;
    }

    pub fn fail_capture_open(&self, why: &str) {
        self.state.lock().unwrap().open_failure = Some(why.into());
    }

    pub fn fail_injection(&self, why: &str) {
        self.state.lock().unwrap().inject_failure = Some(why.into());
    }

    pub fn actions(&self) -> Vec<Action> {
        self.state.lock().unwrap().actions.clone()
    }

    pub fn injected(&self) -> Vec<String> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Injected { text, .. } => Some(text),
                _ => None,
            })
            .collect()
    }

    /// What each injection was told the binding was still holding down, in order.
    pub fn held_during_injection(&self) -> Vec<Option<KeyCode>> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Injected { held, .. } => Some(held),
                _ => None,
            })
            .collect()
    }

    pub fn sent(&self) -> Vec<TranscriptionRequest> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Sent(request) => Some(request),
                _ => None,
            })
            .collect()
    }

    pub fn notifications(&self) -> Vec<String> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Notified(text) => Some(text),
                _ => None,
            })
            .collect()
    }

    pub fn tray_states(&self) -> Vec<TrayState> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Tray(state) => Some(state),
                _ => None,
            })
            .collect()
    }

    /// Waits until the recorded actions satisfy `predicate`, or fails the test with everything
    /// recorded so far. Tests synchronise on what the speech typist did, never on a sleep.
    pub async fn wait_until(&self, what: &str, predicate: impl Fn(&[Action]) -> bool) {
        let wait = async {
            loop {
                let notified = self.changed.notified();
                if predicate(&self.actions()) {
                    return;
                }
                notified.await;
            }
        };
        if tokio::time::timeout(std::time::Duration::from_secs(5), wait).await.is_err() {
            panic!("timed out waiting for {what}; recorded: {:#?}", self.actions());
        }
    }

    /// How each injection was asked to make the words arrive, in order.
    pub fn injection_methods(&self) -> Vec<InjectionMethod> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Injected { method, .. } => Some(method),
                _ => None,
            })
            .collect()
    }

    /// The keys the core last told the host to watch and swallow.
    pub fn watched_keys(&self) -> Vec<KeyCode> {
        self.actions()
            .into_iter()
            .filter_map(|a| match a {
                Action::Bindings(keys) => Some(keys),
                _ => None,
            })
            .next_back()
            .unwrap_or_default()
    }

    /// Waits for a dictation to be wholly over: the tray back to idle with nothing left in flight.
    pub async fn wait_for_idle(&self) {
        self.wait_until("the dictation to finish", |actions| {
            matches!(actions.last(), Some(Action::Tray(TrayState::Idle)))
        })
        .await;
    }

    fn record(&self, action: Action) {
        self.state.lock().unwrap().actions.push(action);
        self.changed.notify_waiters();
    }
}

#[async_trait]
impl Host for FakeHost {
    fn open_capture(&self) -> Result<CaptureFormat, HostError> {
        let (failure, sample_rate) = {
            let state = self.state.lock().unwrap();
            (state.open_failure.clone(), state.sample_rate)
        };
        if let Some(why) = failure {
            return Err(HostError(why));
        }
        self.record(Action::CaptureOpened);
        Ok(CaptureFormat { sample_rate })
    }

    fn close_capture(&self) {
        self.record(Action::CaptureClosed);
    }

    fn foreground_window(&self) -> WindowId {
        WindowId(self.state.lock().unwrap().foreground)
    }

    fn inject(&self, injection: Injection<'_>) -> Result<(), HostError> {
        if let Some(why) = self.state.lock().unwrap().inject_failure.clone() {
            return Err(HostError(why));
        }
        self.record(Action::Injected {
            text: injection.text.to_string(),
            held: injection.held,
            method: injection.method,
        });
        Ok(())
    }

    fn set_bindings(&self, keys: &[KeyCode]) {
        self.record(Action::Bindings(keys.to_vec()));
    }

    fn set_tray(&self, state: TrayState) {
        self.record(Action::Tray(state));
    }

    fn play_cue(&self, cue: Cue) {
        self.record(Action::Cue(cue));
    }

    fn notify(&self, message: &str) {
        self.record(Action::Notified(message.to_string()));
    }

    async fn transcribe(
        &self,
        request: TranscriptionRequest,
    ) -> Result<Transcript, TranscribeError> {
        let answer = {
            let mut state = self.state.lock().unwrap();
            state.actions.push(Action::Sent(request));
            state.answers.pop_front()
        };
        self.changed.notify_waiters();
        answer.unwrap_or_else(|| Ok(Transcript::words("")))
    }
}
