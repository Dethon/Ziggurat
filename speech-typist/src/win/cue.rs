//! The two cue sounds, generated rather than shipped.
//!
//! Two short tones built at startup and played straight out of memory: nothing on disk, nothing
//! embedded, and one fewer file beside the executable that is meant to be one file.

use windows::Win32::Media::Audio::{PlaySoundW, SND_ASYNC, SND_MEMORY, SND_NODEFAULT};

use crate::host::Cue;
use crate::wav;

const RATE: u32 = 22_050;

pub struct Cues {
    start: Vec<u8>,
}

impl Cues {
    pub fn new() -> Self {
        Self { start: tone(880.0, 70) }
    }

    pub fn play(&self, cue: Cue) {
        let wav = match cue {
            Cue::Start => &self.start,
        };
        // SND_ASYNC so a cue never delays opening the microphone, SND_NODEFAULT so a failure is
        // silence rather than Windows' own ding. SND_ASYNC reads the buffer after this returns,
        // which is safe because `Cues` lives as long as the process does.
        unsafe {
            let _ = PlaySoundW(
                windows::core::PCWSTR(wav.as_ptr() as *const u16),
                None,
                SND_MEMORY | SND_ASYNC | SND_NODEFAULT,
            );
        }
    }
}

/// A sine with a short fade either end, because a square-edged tone clicks on cheap speakers.
fn tone(hz: f32, ms: u32) -> Vec<u8> {
    let samples = (RATE * ms / 1000) as usize;
    let fade = samples / 8;
    let pcm: Vec<i16> = (0..samples)
        .map(|i| {
            let envelope = (i.min(samples - i - 1).min(fade) as f32 / fade as f32).min(1.0);
            let phase = i as f32 / RATE as f32 * hz * std::f32::consts::TAU;
            (phase.sin() * envelope * 6_000.0) as i16
        })
        .collect();
    wav::from_pcm(&pcm, RATE)
}
