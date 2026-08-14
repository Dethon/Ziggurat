//! The dictation itself: a binding goes down, frames accumulate, segments are cut where the
//! person paused, each becomes a transcript, and each transcript is typed into the window the
//! dictation began in front of.
//!
//! Everything outward goes through [`Host`], which is what makes all of this testable in WSL.

use std::collections::VecDeque;
use std::sync::Arc;

use tokio::sync::mpsc;

use crate::config::Config;
use crate::detector::{EnergyDetector, SegmentDetector};
use crate::host::{
    Cue, Host, HostEvent, KeyCode, TranscribeError, Transcript, TranscriptionRequest, TrayState,
    WindowId,
};
use crate::prompt;
use crate::wav;

#[cfg(test)]
mod tests;

/// One request in flight at a time, strictly in segment order. That is what makes prompt chaining
/// possible and removes any need for a reorder buffer.
struct Attempt {
    request: TranscriptionRequest,
    tries: u8,
}

struct Live {
    key: KeyCode,
    binding: usize,
    target: WindowId,
    sample_rate: u32,
    started_ms: u64,
    /// Segments cut but not yet asked of Lemonade.
    waiting: VecDeque<Vec<i16>>,
    inflight: Option<Attempt>,
    /// The key came up (or the watchdog fired): no more audio, but the queue still drains.
    ended: bool,
    /// The window in front stopped being the target: nothing more from this dictation is typed.
    abandoned: bool,
    /// One notification per dictation, not one per segment.
    told: bool,
    /// The previous segment's transcript, for the prompt chain. A segment that produced nothing
    /// leaves it alone rather than clearing it.
    previous: Option<String>,
    injected_any: bool,
    /// Where the audio is cut into segments. Held by the dictation rather than by the core, so a
    /// detector never carries state from one dictation into the next.
    detector: Box<dyn SegmentDetector>,
}

struct Core {
    host: Arc<dyn Host>,
    config: Config,
    live: Option<Live>,
    /// Survives the dictation it happened in: the error state clears on the next transcript that
    /// arrives, so it never needs a manual reset.
    failing: bool,
    tray: TrayState,
    now_ms: u64,
}

/// What a finished request carries back to the loop.
struct Done(Result<Transcript, TranscribeError>);

pub async fn run(host: Arc<dyn Host>, mut events: mpsc::Receiver<HostEvent>, config: Config) {
    let mut core = Core {
        host,
        config,
        live: None,
        failing: false,
        tray: TrayState::Idle,
        now_ms: 0,
    };
    let (done_tx, mut done_rx) = mpsc::channel::<Done>(4);

    loop {
        tokio::select! {
            event = events.recv() => match event {
                None | Some(HostEvent::Quit) => break,
                Some(event) => core.on_event(event, &done_tx),
            },
            Some(done) = done_rx.recv() => core.on_transcribed(done, &done_tx),
        }
    }
    core.abandon_capture();
}

impl Core {
    fn on_event(&mut self, event: HostEvent, done: &mpsc::Sender<Done>) {
        match event {
            HostEvent::BindingDown(key) => self.on_down(key, done),
            HostEvent::BindingUp(key) => self.on_up(key, done),
            HostEvent::Frame(samples) => self.on_frame(samples, done),
            HostEvent::Tick { at_ms } => self.on_tick(at_ms, done),
            HostEvent::Quit => {}
        }
    }

    fn on_down(&mut self, key: KeyCode, _done: &mpsc::Sender<Done>) {
        // A second binding pressed while one is live is ignored: two languages must never
        // interleave into the same window, and the first dictation carries on undisturbed.
        if self.live.is_some() {
            return;
        }
        let Some(binding) = self.config.binding_for(key) else {
            return;
        };

        let format = match self.host.open_capture() {
            Ok(format) => format,
            Err(error) => {
                self.host.notify(&format!("Could not open the microphone: {error}"));
                self.failing = true;
                self.refresh_tray();
                return;
            }
        };
        // After the device is open, so it means "speak now" rather than "key received".
        if self.config.audio.cues.enabled {
            self.host.play_cue(Cue::Start);
        }

        self.live = Some(Live {
            key,
            binding,
            target: self.host.foreground_window(),
            sample_rate: format.sample_rate,
            started_ms: self.now_ms,
            waiting: VecDeque::new(),
            inflight: None,
            ended: false,
            abandoned: false,
            told: false,
            previous: None,
            injected_any: false,
            detector: Box::new(EnergyDetector::new(&self.config.detector, format.sample_rate)),
        });
        self.refresh_tray();
    }

    fn on_up(&mut self, key: KeyCode, done: &mpsc::Sender<Done>) {
        if self.live.as_ref().is_none_or(|live| live.key != key) {
            return;
        }
        self.end_recording();
        self.pump(done);
    }

    fn on_frame(&mut self, samples: Vec<i16>, done: &mpsc::Sender<Done>) {
        let Some(live) = self.live.as_mut() else {
            return;
        };
        if live.ended || live.abandoned {
            return;
        }
        live.waiting.extend(live.detector.push(&samples));
        self.pump(done);
    }

    fn on_tick(&mut self, at_ms: u64, done: &mpsc::Sender<Done>) {
        self.now_ms = at_ms;
        let watchdog_ms = self.config.injection.watchdog_secs * 1_000;
        let expired = self
            .live
            .as_ref()
            .is_some_and(|live| !live.ended && at_ms.saturating_sub(live.started_ms) >= watchdog_ms);
        if expired {
            // A key-up lost to a remote desktop session, fast user switching or a hook that lost
            // its window would otherwise hold the microphone for as long as the process lives.
            self.end_recording();
            self.pump(done);
        }
    }

    /// The key came up, or the watchdog fired. The audio stops here; the queue still drains.
    fn end_recording(&mut self) {
        let cues = self.config.audio.cues.enabled;
        let Some(live) = self.live.as_mut() else {
            return;
        };
        if live.ended {
            return;
        }
        live.ended = true;
        live.waiting.extend(live.detector.flush());
        self.host.close_capture();
        if cues {
            self.host.play_cue(Cue::Stop);
        }
    }

    /// Starts the next request if there is one and nothing is in flight, then settles the tray and
    /// clears the dictation once there is nothing left to do.
    fn pump(&mut self, done: &mpsc::Sender<Done>) {
        self.start_next(done);
        let finished = self.live.as_ref().is_some_and(|live| {
            live.ended && live.inflight.is_none() && live.waiting.is_empty()
        });
        if finished {
            self.live = None;
        }
        self.refresh_tray();
    }

    fn start_next(&mut self, done: &mpsc::Sender<Done>) {
        let Some(live) = self.live.as_mut() else {
            return;
        };
        if live.inflight.is_some() || live.abandoned {
            return;
        }
        let Some(segment) = live.waiting.pop_front() else {
            return;
        };
        let binding = &self.config.bindings[live.binding];
        let request = TranscriptionRequest {
            wav: wav::from_pcm(&segment, live.sample_rate),
            language: binding.language.clone(),
            // The chain reads `previous`, which only an accepted transcript writes — so a segment
            // that produced nothing is skipped rather than stalling the ones after it.
            prompt: prompt::compose(
                &binding.vocabulary,
                live.previous.as_deref(),
                self.config.lemonade.max_prompt_chars,
            ),
        };
        live.inflight = Some(Attempt { request: request.clone(), tries: 1 });
        self.send(request, done);
    }

    fn send(&self, request: TranscriptionRequest, done: &mpsc::Sender<Done>) {
        let host = self.host.clone();
        let done = done.clone();
        tokio::spawn(async move {
            let result = host.transcribe(request).await;
            let _ = done.send(Done(result)).await;
        });
    }

    fn on_transcribed(&mut self, Done(result): Done, done: &mpsc::Sender<Done>) {
        let Some(live) = self.live.as_mut() else {
            return;
        };
        let Some(attempt) = live.inflight.take() else {
            return;
        };

        match result {
            Ok(transcript) => {
                self.failing = false;
                self.accept(transcript);
            }
            Err(error) => {
                // Retried exactly once, then dropped: one blip must not cost the whole dictation.
                if attempt.tries < 2 {
                    let request = attempt.request.clone();
                    live.inflight = Some(Attempt { request: request.clone(), tries: attempt.tries + 1 });
                    self.send(request, done);
                    self.refresh_tray();
                    return;
                }
                self.fail(error);
            }
        }
        self.pump(done);
    }

    fn accept(&mut self, transcript: Transcript) {
        let text = transcript.text.trim().to_string();
        // Dropping a segment is not an error and raises nothing: it is the gate working.
        if text.is_empty() || self.hallucinated(&transcript) {
            return;
        }
        let Some(live) = self.live.as_mut() else {
            return;
        };

        // Words in the wrong window are worse than missing words, so the target is checked
        // immediately before typing rather than when the segment was cut.
        if self.host.foreground_window() != live.target {
            live.abandoned = true;
            live.waiting.clear();
            live.ended = true;
            if !live.told {
                live.told = true;
                self.host.notify("The window changed, so the rest of the dictation was dropped.");
            }
            self.host.close_capture();
            return;
        }

        let joined = if live.injected_any { format!(" {text}") } else { text.clone() };
        // Still held means the binding's key is logically down while these characters are sent.
        let held = (!live.ended).then_some(live.key);
        if let Err(error) = self.host.inject(&joined, held) {
            self.tell_once(&format!("Could not type the transcript: {error}"));
            self.failing = true;
            return;
        }
        let live = self.live.as_mut().expect("a live dictation cannot end during injection");
        live.injected_any = true;
        live.previous = Some(text);
    }

    /// Whisper hallucinates on quiet audio — a stock "Thank you.", subtitle credits — and without
    /// this the fan, the air conditioning and a mechanical keyboard put words nobody said into a
    /// person's document.
    ///
    /// A signal that is absent or malformed means no signal, and the transcript is typed anyway.
    /// Failing open is the same choice the .NET client makes and for the same reason: a
    /// shortcoming in the response must never silently swallow words that were actually said.
    fn hallucinated(&self, transcript: &Transcript) -> bool {
        let gate = &self.config.gate;
        transcript.no_speech_prob.is_some_and(|p| p > gate.max_no_speech_prob)
            || transcript.avg_logprob.is_some_and(|p| p < gate.min_avg_logprob)
    }

    fn fail(&mut self, error: TranscribeError) {
        self.failing = true;
        self.tell_once(&format!("Dictation failed: {error}"));
    }

    /// One notification per dictation: with Lemonade down every segment fails, and a notification
    /// each would be a stream of them while the person is still speaking.
    fn tell_once(&mut self, message: &str) {
        let told = self.live.as_ref().is_some_and(|live| live.told);
        if told {
            return;
        }
        if let Some(live) = self.live.as_mut() {
            live.told = true;
        }
        self.host.notify(message);
    }

    fn refresh_tray(&mut self) {
        let wanted = match &self.live {
            _ if self.failing => TrayState::Error,
            Some(live) if !live.ended => TrayState::Recording,
            Some(_) => TrayState::Transcribing,
            None => TrayState::Idle,
        };
        if wanted != self.tray {
            self.tray = wanted;
            self.host.set_tray(wanted);
        }
    }

    /// Quitting mid-dictation must not leave the microphone held.
    fn abandon_capture(&mut self) {
        if self.live.as_ref().is_some_and(|live| !live.ended) {
            self.host.close_capture();
        }
        self.live = None;
    }
}
