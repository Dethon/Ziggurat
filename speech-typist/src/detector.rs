//! Where a dictation is cut into segments.
//!
//! Behind its own small interface so a model-based detector can replace this one later without
//! the core changing at all. Nothing here knows what a transcript is.

use crate::config::DetectorConfig;

/// Cuts a stream of frames into segments. Everything not yet cut stays inside the detector.
pub trait SegmentDetector: Send {
    /// Feeds audio and answers whatever it completed a segment of, in order.
    fn push(&mut self, samples: &[i16]) -> Vec<Vec<i16>>;

    /// The dictation ended. Answers what is left if it holds speech, and nothing if it does not:
    /// a dictation in which nothing was said asks nothing of Lemonade.
    fn flush(&mut self) -> Option<Vec<i16>>;
}

/// An energy detector with hysteresis: it takes `speech_rms` to start counting as speech and
/// stays counting until the audio drops below the lower `silence_rms`, so a quiet syllable
/// mid-phrase does not cut where the person was still talking.
pub struct EnergyDetector {
    window: usize,
    speech_rms: f32,
    silence_rms: f32,
    silence_cut: usize,
    padding: usize,
    max_segment: usize,
    force_search: usize,
    buffer: Vec<i16>,
    analysed: usize,
    in_speech: bool,
    silence_run: usize,
}

/// Energy is judged over 20 ms of audio: long enough that one glottal closure is not silence,
/// short enough that a cut lands inside the pause rather than after it.
const WINDOW_MS: usize = 20;

impl EnergyDetector {
    pub fn new(config: &DetectorConfig, sample_rate: u32) -> Self {
        let per_ms = |ms: usize| (sample_rate as usize * ms / 1000).max(1);
        Self {
            window: per_ms(WINDOW_MS),
            speech_rms: config.speech_rms,
            silence_rms: config.silence_rms,
            silence_cut: per_ms(config.silence_cut_ms as usize),
            padding: per_ms(config.padding_ms as usize),
            max_segment: per_ms(config.max_segment_ms as usize),
            force_search: per_ms(config.force_cut_search_ms as usize),
            buffer: Vec::new(),
            analysed: 0,
            in_speech: false,
            silence_run: 0,
        }
    }
}

impl EnergyDetector {
    /// The pause was long enough. The segment keeps `padding` of the pause after the speech
    /// ended, and everything else is thrown away — the next segment's lead-in comes from the
    /// rolling pre-roll instead, which is what keeps a plosive at the start of the next phrase.
    fn cut_at_pause(&mut self) -> Vec<i16> {
        let speech_end = self.analysed.saturating_sub(self.silence_run);
        let end = (speech_end + self.padding).min(self.buffer.len());
        let segment = self.buffer[..end].to_vec();
        self.buffer.clear();
        self.analysed = 0;
        self.in_speech = false;
        self.silence_run = 0;
        segment
    }

    /// Nobody paused, and the next second of audio would run past whisper's window. Cut at the
    /// quietest point in the trailing search region, so the split lands in a gap between words.
    fn force_cut(&mut self) -> Vec<i16> {
        let search_from = self.buffer.len().saturating_sub(self.force_search);
        let quietest = (search_from..self.buffer.len().saturating_sub(self.window))
            .step_by(self.window)
            .min_by(|&a, &b| {
                rms(&self.buffer[a..a + self.window])
                    .total_cmp(&rms(&self.buffer[b..b + self.window]))
            })
            .unwrap_or(search_from);
        let cut = quietest + self.window / 2;

        let end = (cut + self.padding).min(self.buffer.len());
        let segment = self.buffer[..end].to_vec();
        // The audio after the cut is speech, not a pause, so it starts the next segment rather
        // than being thrown away — with padding either side, as at a pause.
        let keep_from = cut.saturating_sub(self.padding);
        self.buffer.drain(..keep_from);
        self.analysed = self.analysed.saturating_sub(keep_from);
        self.silence_run = 0;
        segment
    }

    /// While nothing is being said the buffer is a rolling `padding` of audio and no more, so a
    /// long pause costs nothing and the segment that follows it still opens with a lead-in.
    fn trim_preroll(&mut self) {
        if self.buffer.len() > self.padding {
            let drop = self.buffer.len() - self.padding;
            self.buffer.drain(..drop);
            self.analysed = self.analysed.saturating_sub(drop);
        }
    }
}

impl SegmentDetector for EnergyDetector {
    fn push(&mut self, samples: &[i16]) -> Vec<Vec<i16>> {
        self.buffer.extend_from_slice(samples);
        let mut cuts = Vec::new();

        while self.analysed + self.window <= self.buffer.len() {
            let loudness = rms(&self.buffer[self.analysed..self.analysed + self.window]);
            self.analysed += self.window;

            if !self.in_speech {
                if loudness >= self.speech_rms {
                    self.in_speech = true;
                    self.silence_run = 0;
                }
                continue;
            }

            if loudness < self.silence_rms {
                self.silence_run += self.window;
            } else {
                self.silence_run = 0;
            }

            if self.silence_run >= self.silence_cut {
                cuts.push(self.cut_at_pause());
            } else if self.buffer.len() >= self.max_segment {
                cuts.push(self.force_cut());
            }
        }

        if !self.in_speech {
            self.trim_preroll();
        }
        cuts
    }

    fn flush(&mut self) -> Option<Vec<i16>> {
        let held = self.in_speech.then(|| std::mem::take(&mut self.buffer));
        self.buffer.clear();
        self.analysed = 0;
        self.in_speech = false;
        self.silence_run = 0;
        held
    }
}

/// i16 amplitude units, the units every threshold in [`DetectorConfig`] is written in.
pub fn rms(samples: &[i16]) -> f32 {
    if samples.is_empty() {
        return 0.0;
    }
    let energy: f64 =
        samples.iter().map(|&s| s as f64 * s as f64).sum::<f64>() / samples.len() as f64;
    energy.sqrt() as f32
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: u32 = 16_000;

    fn ms(samples: usize) -> usize {
        samples * 1000 / RATE as usize
    }

    fn samples(ms: usize) -> usize {
        RATE as usize * ms / 1000
    }

    /// A square wave, so RMS means for this what it means for real audio.
    fn tone(ms: usize, amplitude: i16) -> Vec<i16> {
        (0..samples(ms)).map(|i| if i % 8 < 4 { amplitude } else { -amplitude }).collect()
    }

    fn speech(ms: usize) -> Vec<i16> {
        tone(ms, 4_000)
    }

    fn quiet(ms: usize) -> Vec<i16> {
        tone(ms, 20)
    }

    fn detector() -> EnergyDetector {
        EnergyDetector::new(&DetectorConfig::default(), RATE)
    }

    #[test]
    fn a_pause_cuts_a_segment() {
        let mut detector = detector();

        assert!(detector.push(&speech(1_000)).is_empty(), "still talking");
        let cuts = detector.push(&quiet(600));

        assert_eq!(cuts.len(), 1);
        assert!(detector.flush().is_none(), "nothing but the pause is left");
    }

    #[test]
    fn the_cut_keeps_padding_after_the_speech_rather_than_landing_on_the_last_syllable() {
        // 400 ms below the threshold is what cuts, and cutting there would clip the tail of the
        // word that ended. The segment keeps ~200 ms of the pause instead.
        let mut detector = detector();
        detector.push(&speech(1_000));

        let cuts = detector.push(&quiet(600));

        let length = ms(cuts[0].len());
        assert!(
            (1_150..=1_300).contains(&length),
            "expected a second of speech plus ~200 ms of padding, got {length} ms"
        );
    }

    #[test]
    fn the_segment_after_a_pause_keeps_its_lead_in_so_a_plosive_is_not_clipped() {
        // The detector only knows speech started once a whole window crossed the threshold, so
        // without a rolling pre-roll every segment would begin mid-attack.
        let mut detector = detector();
        detector.push(&speech(600));
        detector.push(&quiet(600));

        detector.push(&quiet(1_000)); // a long pause: the pre-roll must not grow with it
        detector.push(&speech(600));
        let last = detector.flush().unwrap();

        let length = ms(last.len());
        assert!(
            (620..=850).contains(&length),
            "expected the speech plus a bounded lead-in, got {length} ms"
        );
    }

    #[test]
    fn a_dictation_of_silence_holds_no_segment_at_all() {
        let mut detector = detector();

        assert!(detector.push(&quiet(5_000)).is_empty());
        assert!(detector.flush().is_none());
    }

    #[test]
    fn a_quiet_syllable_inside_a_phrase_does_not_cut_it() {
        // Hysteresis: speech is entered at speech_rms and only left below the lower silence_rms,
        // and a dip has to last silence_cut_ms anyway.
        let mut detector = detector();
        detector.push(&speech(500));

        let cuts = detector.push(&tone(300, 400)); // between the two thresholds, and short
        let more = detector.push(&speech(500));

        assert!(cuts.is_empty() && more.is_empty(), "the phrase was cut in the middle");
    }

    #[test]
    fn a_monologue_with_no_pause_is_force_cut_inside_whispers_window() {
        // Whisper's window is 30 s. Without this, speaking for a minute straight would silently
        // lose everything after the first half-minute.
        let mut detector = detector();

        let mut cuts = Vec::new();
        for _ in 0..30 {
            cuts.extend(detector.push(&speech(1_000)));
        }

        assert!(!cuts.is_empty(), "30 s of unbroken speech was never cut");
        for segment in &cuts {
            assert!(ms(segment.len()) < 30_000, "a segment ran past whisper's window");
        }
    }

    #[test]
    fn the_forced_cut_lands_at_the_quietest_point_it_could_find() {
        // A split mid-syllable is what makes a forced cut read badly, so it is placed in the gap
        // between words: the quietest window in the second before the limit.
        let config = DetectorConfig {
            max_segment_ms: 2_000,
            force_cut_search_ms: 1_000,
            ..DetectorConfig::default()
        };
        let mut detector = EnergyDetector::new(&config, RATE);

        detector.push(&speech(1_200));
        detector.push(&tone(100, 100)); // the gap between two words, at 1.2-1.3 s
        let cuts = detector.push(&speech(900));

        assert_eq!(cuts.len(), 1);
        let length = ms(cuts[0].len());
        assert!(
            (1_200..=1_550).contains(&length),
            "the cut should sit in the gap at 1.2-1.3 s, got {length} ms"
        );
    }

    #[test]
    fn the_audio_after_a_forced_cut_is_kept_rather_than_thrown_away() {
        let config = DetectorConfig { max_segment_ms: 2_000, ..DetectorConfig::default() };
        let mut detector = EnergyDetector::new(&config, RATE);

        detector.push(&speech(1_200));
        detector.push(&tone(100, 100));
        detector.push(&speech(900));
        let rest = detector.flush().expect("the monologue carried on past the cut");

        assert!(ms(rest.len()) > 500, "only {} ms survived the forced cut", ms(rest.len()));
    }

    #[test]
    fn thresholds_and_windows_come_from_config_rather_than_being_baked_in() {
        // The same audio, read by a detector tuned for a louder room, is not speech at all.
        let deaf = DetectorConfig { speech_rms: 20_000.0, silence_rms: 19_000.0, ..DetectorConfig::default() };
        let mut detector = EnergyDetector::new(&deaf, RATE);

        detector.push(&speech(1_000));

        assert!(detector.flush().is_none());
    }

    #[test]
    fn a_frame_shorter_than_the_analysis_window_is_still_heard() {
        // A host is free to deliver 5 ms frames; the detector accumulates rather than dropping.
        let mut detector = detector();

        for _ in 0..200 {
            detector.push(&speech(5));
        }

        assert!(detector.flush().is_some());
    }
}
