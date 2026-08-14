use std::sync::Arc;

use tokio::sync::mpsc;
use tokio::task::JoinHandle;

use crate::config::{Binding, Config, DEFAULT_BINDING_KEY};
use crate::host::{Cue, HostEvent, KeyCode, TrayState};
use crate::testing::{Action, FakeHost};

const SPANISH: KeyCode = DEFAULT_BINDING_KEY;
const RATE: u32 = 16_000;

/// Drives the core the way a real host would: events in, and everything observable recorded by
/// the fake host on the way out.
struct Driver {
    host: Arc<FakeHost>,
    events: mpsc::Sender<HostEvent>,
    running: Option<JoinHandle<()>>,
}

impl Driver {
    fn start(config: Config) -> Self {
        Self::start_with(FakeHost::new(), config)
    }

    fn start_with(host: Arc<FakeHost>, config: Config) -> Self {
        let (events, rx) = mpsc::channel(256);
        let running = tokio::spawn(super::run(host.clone(), rx, config));
        Self { host, events, running: Some(running) }
    }

    async fn send(&self, event: HostEvent) {
        self.events.send(event).await.expect("core stopped listening");
    }

    async fn hold(&self, key: KeyCode, frames: &[Vec<i16>]) {
        self.send(HostEvent::BindingDown(key)).await;
        for frame in frames {
            self.send(HostEvent::Frame(frame.clone())).await;
        }
        self.send(HostEvent::BindingUp(key)).await;
    }

    async fn stop(mut self) {
        self.send(HostEvent::Quit).await;
        self.running.take().unwrap().await.expect("the core panicked");
    }
}

/// A frame of audio loud enough to count as speech, in the same i16 amplitude units the detector
/// thresholds are expressed in.
fn speech(ms: u32) -> Vec<i16> {
    tone(ms, 4_000)
}

fn silence(ms: u32) -> Vec<i16> {
    tone(ms, 20)
}

/// A square-ish wave rather than a constant, so RMS means what it means for real audio.
fn tone(ms: u32, amplitude: i16) -> Vec<i16> {
    let samples = (RATE as u64 * ms as u64 / 1000) as usize;
    (0..samples).map(|i| if i % 8 < 4 { amplitude } else { -amplitude }).collect()
}

fn one_spanish_binding() -> Config {
    Config { bindings: vec![Binding::default()], ..Config::default() }
}

// ── Ticket 02: a held binding becomes typing ───────────────────────────────────────────────────

#[tokio::test]
async fn a_held_binding_becomes_typing_in_the_window_in_front() {
    let host = FakeHost::new();
    host.will_say("hola que tal");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(500)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["hola que tal"]);
    driver.stop().await;
}

#[tokio::test]
async fn the_capture_device_is_open_only_while_the_binding_is_held() {
    let host = FakeHost::new();
    host.will_say("hola");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(500)]).await;
    host.wait_for_idle().await;

    let opens = host.actions().iter().filter(|a| **a == Action::CaptureOpened).count();
    let closes = host.actions().iter().filter(|a| **a == Action::CaptureClosed).count();
    assert_eq!((opens, closes), (1, 1));
    driver.stop().await;
}

#[tokio::test]
async fn the_start_cue_means_speak_now_so_it_follows_the_device_opening() {
    let host = FakeHost::new();
    host.will_say("hola");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(500)]).await;
    host.wait_for_idle().await;

    let order: Vec<_> = host
        .actions()
        .into_iter()
        .filter(|a| matches!(a, Action::CaptureOpened | Action::Cue(Cue::Start)))
        .collect();
    assert_eq!(order, [Action::CaptureOpened, Action::Cue(Cue::Start)]);
    driver.stop().await;
}

#[tokio::test]
async fn cues_can_be_turned_off_without_giving_up_anything_else() {
    let host = FakeHost::new();
    host.will_say("hola");
    let mut config = one_spanish_binding();
    config.audio.cues.enabled = false;
    let driver = Driver::start_with(host.clone(), config);

    driver.hold(SPANISH, &[speech(500)]).await;
    host.wait_for_idle().await;

    assert!(!host.actions().iter().any(|a| matches!(a, Action::Cue(_))));
    assert_eq!(host.injected(), ["hola"]);
    driver.stop().await;
}

#[tokio::test]
async fn a_dictation_with_nothing_said_injects_nothing_and_is_not_an_error() {
    let host = FakeHost::new();
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[silence(1_500)]).await;
    host.wait_for_idle().await;

    assert!(host.injected().is_empty(), "injected: {:?}", host.injected());
    assert!(host.sent().is_empty(), "nothing should have been asked of Lemonade");
    assert!(host.notifications().is_empty());
    assert!(!host.tray_states().contains(&TrayState::Error));
    driver.stop().await;
}

#[tokio::test]
async fn the_wav_sent_declares_the_rate_the_device_opened_at() {
    let host = FakeHost::new();
    host.with_sample_rate(48_000).will_say("hola");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[tone_at(48_000, 500, 4_000)]).await;
    host.wait_for_idle().await;

    let wav = &host.sent()[0].wav;
    assert_eq!(&wav[0..4], b"RIFF");
    assert_eq!(u32::from_le_bytes(wav[24..28].try_into().unwrap()), 48_000);
    driver.stop().await;
}

#[tokio::test]
async fn a_key_that_is_not_bound_does_nothing_at_all() {
    let host = FakeHost::new();
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(KeyCode(0xFE), &[speech(500)]).await;
    driver.send(HostEvent::Tick { at_ms: 1 }).await;

    driver.stop().await;
    assert!(host.actions().is_empty(), "recorded: {:?}", host.actions());
}

// ── Ticket 05: segmenting and progressive injection ───────────────────────────────────────────

/// Two phrases with a pause between them, which is what the detector cuts on.
fn two_phrases() -> Vec<Vec<i16>> {
    vec![speech(800), silence(600), speech(800)]
}

#[tokio::test]
async fn every_segment_after_the_first_is_joined_with_exactly_one_space() {
    let host = FakeHost::new();
    host.will_say("  hola que tal  ").will_say("  todo bien  ");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &two_phrases()).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["hola que tal", " todo bien"]);
    driver.stop().await;
}

#[tokio::test]
async fn segments_are_asked_for_one_at_a_time_and_typed_strictly_in_order() {
    let host = FakeHost::new();
    host.will_say("uno").will_say("dos").will_say("tres");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver
        .hold(
            SPANISH,
            &[speech(800), silence(600), speech(800), silence(600), speech(800)],
        )
        .await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["uno", " dos", " tres"]);
    // One in flight at a time is what makes the ordering an invariant rather than a race, and
    // what makes the prompt chain possible at all.
    let steps: Vec<_> = host
        .actions()
        .into_iter()
        .filter(|a| matches!(a, Action::Sent(_) | Action::Injected(_)))
        .map(|a| matches!(a, Action::Sent(_)))
        .collect();
    assert_eq!(steps, [true, false, true, false, true, false], "a request overlapped an injection");
    driver.stop().await;
}

#[tokio::test]
async fn words_arrive_while_the_key_is_still_held() {
    // The whole point of segmenting: a long dictation reads onto the screen at roughly the speed
    // it is spoken rather than all at once when the key comes up.
    let host = FakeHost::new();
    host.will_say("primera frase");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Frame(silence(600))).await;
    host.wait_until("the first phrase to be typed", |actions| {
        actions.iter().any(|a| matches!(a, Action::Injected(_)))
    })
    .await;

    assert!(
        !host.actions().contains(&Action::CaptureClosed),
        "the key was never released, so the microphone must still be open"
    );
    driver.send(HostEvent::BindingUp(SPANISH)).await;
    host.wait_for_idle().await;
    driver.stop().await;
}

#[tokio::test]
async fn nothing_but_the_surrounding_whitespace_is_rewritten() {
    // Whisper already punctuates and capitalises. A rewrite layer would fight it.
    let host = FakeHost::new();
    host.will_say(" ¿Qué tal, Ziggurat? Todo bien. ");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["¿Qué tal, Ziggurat? Todo bien."]);
    driver.stop().await;
}

#[tokio::test]
async fn a_dictation_that_only_ever_falls_silent_asks_nothing_of_lemonade() {
    let host = FakeHost::new();
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[silence(600), silence(600), silence(600)]).await;
    host.wait_for_idle().await;

    assert!(host.sent().is_empty());
    assert!(host.injected().is_empty());
    driver.stop().await;
}

fn tone_at(rate: u32, ms: u32, amplitude: i16) -> Vec<i16> {
    let samples = (rate as u64 * ms as u64 / 1000) as usize;
    (0..samples).map(|i| if i % 8 < 4 { amplitude } else { -amplitude }).collect()
}
