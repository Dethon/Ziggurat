use std::sync::Arc;

use tokio::sync::mpsc;
use tokio::task::JoinHandle;

use crate::config::{Binding, Config, DEFAULT_BINDING_KEY};
use crate::host::{Cue, HostEvent, KeyCode, TranscribeError, Transcript, TrayState};
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
        .filter(|a| matches!(a, Action::Sent(_) | Action::Injected { .. }))
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
        actions.iter().any(|a| matches!(a, Action::Injected { .. }))
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

// ── Ticket 06: prompt chaining and vocabulary ─────────────────────────────────────────────────

fn with_vocabulary(vocabulary: &str) -> Config {
    Config {
        bindings: vec![Binding { vocabulary: vocabulary.into(), ..Binding::default() }],
        ..Config::default()
    }
}

#[tokio::test]
async fn the_first_segment_carries_the_vocabulary_and_no_chained_text() {
    let host = FakeHost::new();
    host.will_say("hola Ziggurat");
    let driver = Driver::start_with(host.clone(), with_vocabulary("Ziggurat, Lemonade"));

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.sent()[0].prompt.as_deref(), Some("Ziggurat, Lemonade"));
    driver.stop().await;
}

#[tokio::test]
async fn each_later_segment_carries_the_vocabulary_and_then_what_was_just_said() {
    // A phrase that continues across a pause is understood as one thought rather than starting
    // from nothing, and what was said last sits closest to the audio being decoded.
    let host = FakeHost::new();
    host.will_say("estaba diciendo que").will_say("todo funciona");
    let driver = Driver::start_with(host.clone(), with_vocabulary("Ziggurat"));

    driver.hold(SPANISH, &two_phrases()).await;
    host.wait_for_idle().await;

    let prompts: Vec<_> = host.sent().iter().map(|r| r.prompt.clone()).collect();
    assert_eq!(
        prompts,
        [Some("Ziggurat".into()), Some("Ziggurat estaba diciendo que".into())]
    );
    driver.stop().await;
}

#[tokio::test]
async fn a_segment_that_produced_nothing_does_not_poison_the_chain_after_it() {
    // The middle segment fails outright. The third must still be chained to the first, because
    // the alternative is a chain that stalls on the one thing that never produced words.
    let host = FakeHost::new();
    host.will_say("primera")
        .will_answer(Err(TranscribeError::Status(500)))
        .will_answer(Err(TranscribeError::Status(500)))
        .will_say("tercera");
    let driver = Driver::start_with(host.clone(), with_vocabulary("Ziggurat"));

    driver
        .hold(SPANISH, &[speech(800), silence(600), speech(800), silence(600), speech(800)])
        .await;
    host.wait_until("the third segment to be typed", |actions| {
        actions.iter().filter(|a| matches!(a, Action::Injected { .. })).count() == 2
    })
    .await;

    let prompts: Vec<_> = host.sent().iter().map(|r| r.prompt.clone()).collect();
    assert_eq!(
        prompts,
        [
            Some("Ziggurat".into()),
            Some("Ziggurat primera".into()), // the failing segment
            Some("Ziggurat primera".into()), // its one retry
            Some("Ziggurat primera".into()), // the segment after it, chained past the gap
        ]
    );
    assert_eq!(host.injected(), ["primera", " tercera"]);
    driver.stop().await;
}

#[tokio::test]
async fn a_binding_with_no_vocabulary_sends_no_prompt_on_its_first_segment() {
    let host = FakeHost::new();
    host.will_say("hola");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.sent()[0].prompt, None);
    driver.stop().await;
}

// ── Ticket 08: the hallucination gate ─────────────────────────────────────────────────────────

/// Whisper's stock hallucination on near-silence, with the signals that give it away.
fn hallucination(text: &str) -> Transcript {
    Transcript { text: text.into(), avg_logprob: Some(-0.4), no_speech_prob: Some(0.95) }
}

fn confident(text: &str) -> Transcript {
    Transcript { text: text.into(), avg_logprob: Some(-0.3), no_speech_prob: Some(0.05) }
}

#[tokio::test]
async fn a_transcript_whisper_itself_calls_silence_is_dropped_rather_than_typed() {
    // The fan, the air conditioning and a mechanical keyboard otherwise put "Thank you." and
    // subtitle credits into the document.
    let host = FakeHost::new();
    host.will_answer(Ok(hallucination(" Gracias por ver el vídeo.")));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert!(host.injected().is_empty(), "typed: {:?}", host.injected());
    driver.stop().await;
}

#[tokio::test]
async fn a_transcript_whisper_was_only_guessing_at_is_dropped() {
    let host = FakeHost::new();
    host.will_answer(Ok(Transcript {
        text: "algo".into(),
        avg_logprob: Some(-2.5),
        no_speech_prob: Some(0.1),
    }));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert!(host.injected().is_empty(), "typed: {:?}", host.injected());
    driver.stop().await;
}

#[tokio::test]
async fn a_signal_whisper_did_not_send_is_permission_rather_than_refusal() {
    // A shortcoming in the response must never silently swallow words that were actually said,
    // which is the same reason the .NET client fails open.
    let host = FakeHost::new();
    host.will_answer(Ok(Transcript::words("esto sí lo dije")));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["esto sí lo dije"]);
    driver.stop().await;
}

#[tokio::test]
async fn only_the_signal_that_is_there_is_read() {
    let host = FakeHost::new();
    host.will_answer(Ok(Transcript {
        text: "hola".into(),
        avg_logprob: None,
        no_speech_prob: Some(0.95),
    }));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert!(host.injected().is_empty(), "one damning signal is enough");
    driver.stop().await;
}

#[tokio::test]
async fn a_dropped_segment_is_the_gate_working_and_reports_nothing() {
    let host = FakeHost::new();
    host.will_answer(Ok(hallucination(" Thank you.")));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert!(host.notifications().is_empty());
    assert!(!host.tray_states().contains(&TrayState::Error));
    driver.stop().await;
}

#[tokio::test]
async fn a_drop_in_the_middle_does_not_disturb_the_joining_around_it() {
    let host = FakeHost::new();
    host.will_answer(Ok(confident("uno")))
        .will_answer(Ok(hallucination(" Thank you.")))
        .will_answer(Ok(confident("tres")));
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver
        .hold(SPANISH, &[speech(800), silence(600), speech(800), silence(600), speech(800)])
        .await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["uno", " tres"]);
    driver.stop().await;
}

#[tokio::test]
async fn both_thresholds_are_config_keys_and_not_constants() {
    let host = FakeHost::new();
    host.will_answer(Ok(hallucination("lo dije en una habitación ruidosa")));
    let mut config = one_spanish_binding();
    config.gate.max_no_speech_prob = 0.99;
    let driver = Driver::start_with(host.clone(), config);

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["lo dije en una habitación ruidosa"]);
    driver.stop().await;
}

// ── Ticket 09: failure handling ───────────────────────────────────────────────────────────────

fn unreachable() -> Result<Transcript, TranscribeError> {
    Err(TranscribeError::Transport("connection refused".into()))
}

#[tokio::test]
async fn a_failed_request_is_retried_exactly_once() {
    let host = FakeHost::new();
    host.will_answer(unreachable()).will_say("a la segunda");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.sent().len(), 2, "one attempt and one retry");
    assert_eq!(host.injected(), ["a la segunda"]);
    driver.stop().await;
}

#[tokio::test]
async fn after_the_retry_fails_the_segment_is_dropped_and_the_ones_after_it_still_arrive() {
    // One blip must not cost the whole dictation, which is the whole reason the drop is per
    // segment rather than per dictation.
    let host = FakeHost::new();
    host.will_answer(unreachable())
        .will_answer(unreachable())
        .will_say("la segunda frase");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &two_phrases()).await;
    host.wait_until("the second phrase to be typed", |actions| {
        actions.iter().any(|a| matches!(a, Action::Injected { .. }))
    })
    .await;

    assert_eq!(host.sent().len(), 3, "two attempts at the first, one at the second");
    assert_eq!(host.injected(), ["la segunda frase"]);
    driver.stop().await;
}

#[tokio::test]
async fn lemonade_being_down_for_a_whole_dictation_says_so_exactly_once() {
    // With Lemonade down every segment fails, and a notification each would be a stream of them
    // while the person is still speaking.
    let host = FakeHost::new();
    for _ in 0..8 {
        host.will_answer(unreachable());
    }
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver
        .hold(SPANISH, &[speech(800), silence(600), speech(800), silence(600), speech(800)])
        .await;
    host.wait_until("every segment to have failed", |actions| {
        actions.iter().filter(|a| matches!(a, Action::Sent(_))).count() == 6
    })
    .await;

    assert_eq!(host.notifications().len(), 1, "{:?}", host.notifications());
    driver.stop().await;
}

#[tokio::test]
async fn the_tray_says_broken_rather_than_leaving_a_dead_lemonade_looking_slow() {
    let host = FakeHost::new();
    host.will_answer(unreachable()).will_answer(unreachable());
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_until("the tray to show the failure", |actions| {
        actions.contains(&Action::Tray(TrayState::Error))
    })
    .await;

    driver.stop().await;
}

#[tokio::test]
async fn the_error_state_clears_on_the_next_transcript_with_no_manual_action() {
    let host = FakeHost::new();
    host.will_answer(unreachable()).will_answer(unreachable()).will_say("y ahora sí");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_until("the tray to show the failure", |actions| {
        actions.contains(&Action::Tray(TrayState::Error))
    })
    .await;
    driver.hold(SPANISH, &[speech(800)]).await;
    host.wait_for_idle().await;

    assert_eq!(host.injected(), ["y ahora sí"]);
    let last_error = host.tray_states().iter().rposition(|s| *s == TrayState::Error);
    let last_idle = host.tray_states().iter().rposition(|s| *s == TrayState::Idle);
    assert!(last_idle > last_error, "the tray was left broken: {:?}", host.tray_states());
    driver.stop().await;
}

// ── Ticket 10: dictation lifecycle guards ─────────────────────────────────────────────────────

const ENGLISH: KeyCode = KeyCode(0x7D);

fn two_bindings() -> Config {
    Config {
        bindings: vec![
            Binding::default(),
            Binding { key: ENGLISH, language: "en".into(), vocabulary: String::new() },
        ],
        ..Config::default()
    }
}

fn typed_yet(actions: &[Action]) -> usize {
    actions.iter().filter(|a| matches!(a, Action::Injected { .. })).count()
}

#[tokio::test]
async fn a_changed_window_discards_the_rest_and_types_nothing_into_the_new_one() {
    // The tail of a sentence landing in a terminal, a chat box or a game is the failure this
    // exists to prevent: words in the wrong window are worse than missing words.
    let host = FakeHost::new();
    host.will_say("primera").will_say("segunda").will_say("tercera");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Frame(silence(600))).await;
    host.wait_until("the first phrase to be typed", |a| typed_yet(a) == 1).await;

    host.move_to_window(2); // the person let go and clicked into something else
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Frame(silence(600))).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::BindingUp(SPANISH)).await;
    host.wait_until("the discard", |a| a.iter().any(|x| matches!(x, Action::Notified(_)))).await;

    assert_eq!(host.injected(), ["primera"]);
    assert_eq!(host.notifications().len(), 1, "once, not once per remaining segment");
    driver.stop().await;
}

#[tokio::test]
async fn a_dictation_whose_key_up_never_arrives_ends_by_itself_and_closes_the_capture() {
    // A hook can lose a key-up to a remote desktop session or fast user switching. Without this
    // the microphone stays open for the life of the process — a correctness requirement, not a
    // nicety.
    let host = FakeHost::new();
    host.will_say("y entonces me fui");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.send(HostEvent::Tick { at_ms: 0 }).await;
    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Tick { at_ms: 120_000 }).await;
    host.wait_for_idle().await;

    assert!(host.actions().contains(&Action::CaptureClosed));
    assert_eq!(host.injected(), ["y entonces me fui"], "what was said still arrives");
    driver.stop().await;
}

#[tokio::test]
async fn the_watchdog_leaves_a_dictation_that_is_merely_long_alone() {
    let host = FakeHost::new();
    host.will_say("sigo hablando");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.send(HostEvent::Tick { at_ms: 0 }).await;
    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Tick { at_ms: 119_000 }).await;
    driver.send(HostEvent::Frame(silence(600))).await;
    host.wait_until("the phrase to be typed", |a| typed_yet(a) == 1).await;

    assert!(!host.actions().contains(&Action::CaptureClosed), "the key is still held");
    driver.send(HostEvent::BindingUp(SPANISH)).await;
    host.wait_for_idle().await;
    driver.stop().await;
}

#[tokio::test]
async fn a_second_binding_pressed_during_a_live_dictation_is_ignored() {
    // Two languages must never interleave into the same window.
    let host = FakeHost::new();
    host.will_say("en español");
    let driver = Driver::start_with(host.clone(), two_bindings());

    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::BindingDown(ENGLISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::BindingUp(ENGLISH)).await;
    driver.send(HostEvent::BindingUp(SPANISH)).await;
    host.wait_for_idle().await;

    let opens = host.actions().iter().filter(|a| **a == Action::CaptureOpened).count();
    assert_eq!(opens, 1, "the second binding opened a second capture");
    assert_eq!(host.sent()[0].language, "es", "the first dictation carried on undisturbed");
    assert_eq!(host.injected(), ["en español"]);
    driver.stop().await;
}

#[tokio::test]
async fn injection_is_told_which_key_is_still_being_held() {
    // A binding that is itself a modifier would otherwise chord every character it types. Which
    // keys are modifiers is the host's knowledge, so it is handed the key rather than the answer.
    let host = FakeHost::new();
    host.will_say("mientras hablo").will_say("y al soltar");
    let driver = Driver::start_with(host.clone(), one_spanish_binding());

    driver.send(HostEvent::BindingDown(SPANISH)).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::Frame(silence(600))).await;
    host.wait_until("the phrase typed mid-dictation", |a| typed_yet(a) == 1).await;
    driver.send(HostEvent::Frame(speech(800))).await;
    driver.send(HostEvent::BindingUp(SPANISH)).await;
    host.wait_for_idle().await;

    assert_eq!(
        host.held_during_injection(),
        [Some(SPANISH), None],
        "the key was still down for the first and released by the second"
    );
    driver.stop().await;
}

fn tone_at(rate: u32, ms: u32, amplitude: i16) -> Vec<i16> {
    let samples = (rate as u64 * ms as u64 / 1000) as usize;
    (0..samples).map(|i| if i % 8 < 4 { amplitude } else { -amplitude }).collect()
}
