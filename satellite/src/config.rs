use crate::wake::DetectorConfig;
use std::path::PathBuf;

/// Where the activation button comes from. Both impls live behind gpio::ButtonSource.
#[derive(Clone, Debug, PartialEq)]
pub enum ButtonConfig {
    None,
    /// Wired momentary button on a Pi GPIO pin (BCM numbering), via rppal.
    Gpio(u8),
    /// USB foot-switch / button that emits a key event, via the evdev crate.
    /// `key` is the numeric evdev keycode (e.g. 113 = KEY_MUTE, 28 = KEY_ENTER).
    Evdev { device: PathBuf, key: u16 },
}

/// Whether this process drives the satellite's LED. The only backend is the reSpeaker
/// XVF3800's 12-LED WS2812 ring, discovered on USB — so unlike the old GPIO/SPI pins there
/// is nothing to configure: Auto uses the ring when the device is present, --no-led opts out.
#[derive(Clone, Debug, PartialEq)]
pub enum LedConfig {
    Auto,
    None,
}

// --start-delay=100000 (µs): aplay's default start threshold is the FULL 500 ms buffer, so a
// streamed reply isn't audible until 500 ms of audio has been synthesized+delivered; start at
// 100 ms queued instead (buffer stays 500 ms for underrun headroom). -F 50000 reads stdin in
// 50 ms periods so the first write into the ALSA buffer happens sooner.
const DEFAULT_SND_COMMAND: &str =
    "aplay -D plughw:CARD=sndrpihifiberry,DEV=0 -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000";

#[derive(Clone)]
pub struct Config {
    pub listen: String,         // matches Satellites:<id>:Address port (default 10700)
    pub mic_command: String,
    pub snd_command: String,
    // Sink for hub-marked ALERT streams (timers/alarms). Defaults to snd_command, so a unit
    // without a dedicated non-attenuated route behaves exactly as before; music units point it
    // at the `alert` softvol so an alarm bypasses the calibrated `TTS` voice level.
    pub alert_snd_command: String,
    pub detector: DetectorConfig,
    pub wake_enabled: bool,     // --no-wake disables on-device wake (button-only operation)
    // A classifier fire whose wake-word audio (the same `wake_rms` the hub is sent) is quieter
    // than this is dropped on the satellite: no turn, no cue, no LED. 0 = off. It exists because
    // the classifier fires on a near-silent room — on the deployed XVF3800 unit, 33 of the 52
    // wakes that opened the mic onto no speech at all measured under 350, against a quietest
    // real wake of 421 (prod metrics, 2026-09-11..10-10). A measured, per-mic number: this mic's
    // AGC holds speech well above its ~40-110 idle floor; one without AGC needs its own figure.
    pub wake_min_rms: f32,
    pub button: ButtonConfig,
    pub led: LedConfig,         // activity LED; the XVF3800 ring when present, --no-led opts out
    pub preroll_ms: u32,        // zero-lag: how much recent audio to flush to the hub on trigger
    pub wake_preroll_ms: u32,   // wake-path flush: detection-latency gap only, NOT the wake word
    pub awake_cue: bool,
    pub done_cue: bool,
    pub music_mixer: Option<String>,   // ALSA softvol control name; None => duck feature off
    pub music_card: Option<String>,    // amixer -c target where the softvol control lives
    pub duck_percent: u8,              // softvol level while the satellite is active
    pub music_restore_grace_ms: u64,   // hold the un-duck this long so a long reply's inter-segment
                                       // Idle gaps don't flap the music up between segments
    // The satellite's own master output level — the one knob every source ultimately feeds. It is
    // the volume an amp HAT like the MiniAmp does not have in hardware, and is distinct from the
    // per-source ALSA softvols (Music / TTS / Alert), which carry calibration.
    //
    // Two ways to reach it, one per unit type: music units mix in PipeWire, so their master is its
    // sink, driven with wpctl (volume_sink). Voice-only units have no PipeWire and play raw ALSA,
    // so provisioning puts a software softvol in front of their output device and the satellite
    // drives it with amixer (volume_mixer + volume_card, the same control/card pair shape as
    // music_mixer/music_card — but validated rather than ignored when only half is given).
    // Mutually exclusive; all None = feature off.
    pub volume_sink: Option<String>,
    pub volume_mixer: Option<String>,
    pub volume_card: Option<String>,
    pub volume_step: u8, // % of the −51..0 dB range per step: 10 → ten 5.1 dB steps, both backends
}

impl Default for Config {
    fn default() -> Self {
        Self {
            listen: "0.0.0.0:10700".into(),
            // Defaults target the deployed unit: a reSpeaker XVF3800 USB mic array (card `Array`)
            // with a HiFiBerry MiniAmp I2S speaker (card `sndrpihifiberry`). Both device strings
            // are rewritten by provisioning, which auto-detects the capture card by NAME and the
            // speaker by its `sndrpihifiberry*` overlay card — by-name addressing is immune to
            // ALSA index churn (the old `snd_usb_audio index=0` pinning collided with the Pi's
            // built-in vc4-hdmi/headphone cards and was removed). The mic is 16 kHz mono native
            // (no resampling; the XVF3800's 2 capture channels carry the same processed signal, so
            // plughw's stereo->mono averaging is fine); plughw resamples only the 22050 Hz
            // playback. For a reSpeaker 2-Mic HAT pass --mic-command/--snd-command with
            // plughw:CARD=seeed2micvoicec,DEV=0 plus --button-gpio 17; see provisioning.
            // -F 20000 (20 ms period): without it arecord defaults to buffer/4 = 125 ms periods
            // and every mic sample reaches stdout up to 125 ms late — paid on the wake AND the
            // speech->STT path. The 500 ms capture buffer default is independent of -F.
            mic_command: "arecord -D plughw:CARD=Array,DEV=0 -r 16000 -c 1 -f S16_LE -t raw -F 20000".into(),
            snd_command: DEFAULT_SND_COMMAND.into(),
            alert_snd_command: DEFAULT_SND_COMMAND.into(),
            detector: DetectorConfig::default(),
            wake_enabled: true,
            wake_min_rms: 0.0,
            button: ButtonConfig::None, // no button by default; --button-gpio / --button-evdev opt in
            led: LedConfig::Auto, // XVF3800 ring when the device is on USB; absent hardware = LED-less
            preroll_ms: 1000,
            wake_preroll_ms: 240, // covers the ~181 ms measured detection latency with margin
            awake_cue: true,
            done_cue: true,
            music_mixer: None,
            music_card: None,
            duck_percent: 20,
            music_restore_grace_ms: 3000,
            volume_sink: None,
            volume_mixer: None,
            volume_card: None,
            volume_step: 10,
        }
    }
}

impl Config {
    /// Flags: --listen --mic-command --snd-command --alert-snd-command --threshold --no-wake
    ///        --wake-window <n> (alias: --trigger-level) --wake-min-rms <i16 rms, 0 = off>
    ///        --button-gpio <pin> | --button-evdev <device>:<keycode> | --no-button
    ///        --no-led
    ///        --preroll-ms <ms> --wake-preroll-ms <ms> --no-awake-cue --no-done-cue
    ///        --music-mixer <control> --music-card <card> --duck-percent <pct> --music-restore-grace-ms <ms>
    ///        --volume-sink <name> | --volume-mixer <control> [--volume-card <card>]
    ///        --volume-step <pct>
    pub fn from_args() -> anyhow::Result<Self> {
        Self::parse(pico_args::Arguments::from_env())
    }

    fn parse(mut pa: pico_args::Arguments) -> anyhow::Result<Self> {
        let mut c = Config::default();
        if let Some(v) = pa.opt_value_from_str::<_, String>("--listen")? { c.listen = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--mic-command")? { c.mic_command = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--snd-command")? { c.snd_command = v; }
        // Read AFTER --snd-command so the fallback sees the final value: a unit that overrides
        // only the normal sink gets its alerts on that same sink, not on the compiled-in default.
        c.alert_snd_command = pa
            .opt_value_from_str::<_, String>("--alert-snd-command")?
            .unwrap_or_else(|| c.snd_command.clone());
        if let Some(v) = pa.opt_value_from_str::<_, f32>("--threshold")? { c.detector.threshold = v; }
        // --wake-window is the sliding-mean length. --trigger-level is its pre-sliding-window
        // name, still accepted because parse() errors on unknown arguments below: a unit file
        // still carrying the old flag would otherwise fail to start and loop under
        // Restart=always. BOTH are always read, even when --wake-window wins, so a leftover
        // alias can't land in `rest` and trip that same check.
        let wake_window = pa.opt_value_from_str::<_, u32>("--wake-window")?;
        let trigger_level = pa.opt_value_from_str::<_, u32>("--trigger-level")?;
        if let Some(v) = wake_window.or(trigger_level) {
            anyhow::ensure!(v >= 1, "--wake-window must be at least 1 (got {v})");
            c.detector.window = v;
        }
        if let Some(v) = pa.opt_value_from_str::<_, f32>("--wake-min-rms")? {
            anyhow::ensure!(v.is_finite() && v >= 0.0, "--wake-min-rms must be a number >= 0 (got {v})");
            c.wake_min_rms = v;
        }
        if let Some(v) = pa.opt_value_from_str::<_, u32>("--preroll-ms")? { c.preroll_ms = v; }
        if let Some(v) = pa.opt_value_from_str::<_, u32>("--wake-preroll-ms")? { c.wake_preroll_ms = v; }
        if pa.contains("--no-wake") { c.wake_enabled = false; }
        if pa.contains("--no-awake-cue") { c.awake_cue = false; }
        if pa.contains("--no-done-cue") { c.done_cue = false; }
        if pa.contains("--no-button") {
            c.button = ButtonConfig::None;
        } else if let Some(pin) = pa.opt_value_from_str::<_, u8>("--button-gpio")? {
            c.button = ButtonConfig::Gpio(pin);
        } else if let Some(spec) = pa.opt_value_from_str::<_, String>("--button-evdev")? {
            let (dev, key) = spec.rsplit_once(':')
                .ok_or_else(|| anyhow::anyhow!("--button-evdev needs <device>:<keycode>, e.g. /dev/input/event3:28"))?;
            c.button = ButtonConfig::Evdev { device: dev.into(), key: key.parse()? };
        }
        if pa.contains("--no-led") { c.led = LedConfig::None; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--music-mixer")? { c.music_mixer = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--music-card")? { c.music_card = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, u8>("--duck-percent")? { c.duck_percent = v; }
        if let Some(v) = pa.opt_value_from_str::<_, u64>("--music-restore-grace-ms")? { c.music_restore_grace_ms = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--volume-sink")? { c.volume_sink = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--volume-mixer")? { c.volume_mixer = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--volume-card")? { c.volume_card = Some(v); }
        // The two masters are the same knob reached by different tools, so a unit naming both is
        // misconfigured whichever one won: the satellite would still beep its confirmation while
        // moving a level nobody hears — the exact failure a voice-only unit had before it had an
        // ALSA master at all. Reject it here, where the mistake is, rather than picking one.
        anyhow::ensure!(
            !(c.volume_sink.is_some() && c.volume_mixer.is_some()),
            "--volume-sink and --volume-mixer are mutually exclusive: pass the PipeWire sink on a \
             music unit, or the ALSA softvol control on a voice-only one"
        );
        anyhow::ensure!(
            !(c.volume_card.is_some() && c.volume_mixer.is_none()),
            "--volume-card needs --volume-mixer (the control to look up on that card)"
        );
        if let Some(v) = pa.opt_value_from_str::<_, u8>("--volume-step")? {
            anyhow::ensure!(v >= 1, "--volume-step must be at least 1 (got {v})");
            c.volume_step = v;
        }
        let rest = pa.finish();
        anyhow::ensure!(rest.is_empty(), "unknown arguments: {rest:?}");
        Ok(c)
    }

    /// Number of 1280-sample (80 ms) chunks to retain in the pre-roll ring buffer.
    pub fn preroll_chunks(&self) -> usize {
        (self.preroll_ms as usize).div_ceil(80)
    }

    /// Chunks to keep when a WAKE trigger flushes the pre-roll: just the detection-latency
    /// gap after the wake word ends (~181 ms measured) — NOT the wake word itself, which
    /// would otherwise be transcribed and dispatched as the request.
    pub fn wake_preroll_chunks(&self) -> usize {
        (self.wake_preroll_ms as usize).div_ceil(80)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(v: &[&str]) -> pico_args::Arguments {
        pico_args::Arguments::from_vec(v.iter().map(std::ffi::OsString::from).collect())
    }

    #[test]
    fn led_defaults_to_auto() {
        assert_eq!(Config::default().led, LedConfig::Auto);
    }

    // The old --led-spi / --led-gpio flags are gone; passing them must fail loudly rather
    // than being silently ignored, so a stale provisioning ExecStart is caught immediately.
    #[test]
    fn removed_led_flags_are_rejected() {
        assert!(Config::parse(args(&["--led-spi"])).is_err());
        assert!(Config::parse(args(&["--led-gpio", "22"])).is_err());
    }

    #[test]
    fn no_led_flag_parses() {
        let c = Config::parse(args(&["--no-led"])).unwrap();
        assert_eq!(c.led, LedConfig::None);
    }

    #[test]
    fn wake_window_defaults_to_one_and_flag_parses() {
        assert_eq!(Config::default().detector.window, 1);
        let c = Config::parse(args(&["--wake-window", "3"])).unwrap();
        assert_eq!(c.detector.window, 3);
    }

    /// --trigger-level is the pre-sliding-window name, kept as an alias because parse() errors
    /// on unknown arguments: a unit file still carrying the old flag would otherwise fail to
    /// start and loop forever under Restart=always.
    #[test]
    fn trigger_level_alias_still_sets_the_window() {
        let c = Config::parse(args(&["--trigger-level", "3"])).unwrap();
        assert_eq!(c.detector.window, 3);
    }

    #[test]
    fn wake_window_wins_over_the_trigger_level_alias() {
        let c = Config::parse(pico_args::Arguments::from_vec(vec![
            "--wake-window".into(), "4".into(),
            "--trigger-level".into(), "2".into(),
        ]))
        .unwrap();
        assert_eq!(c.detector.window, 4);
    }

    /// Off unless asked for: the floor is a measured, per-microphone number, and a default that
    /// suits one mic chain would silently swallow real wakes on a quieter one.
    #[test]
    fn wake_min_rms_defaults_to_off_and_flag_parses() {
        assert_eq!(Config::default().wake_min_rms, 0.0);
        let c = Config::parse(args(&["--wake-min-rms", "350"])).unwrap();
        assert_eq!(c.wake_min_rms, 350.0);
    }

    /// A negative or non-finite floor has no meaning, and NaN in particular would make every
    /// comparison false — i.e. quietly reject nothing while looking configured.
    #[test]
    fn nonsense_wake_min_rms_is_rejected() {
        assert!(Config::parse(args(&["--wake-min-rms", "-1"])).is_err());
        assert!(Config::parse(args(&["--wake-min-rms", "NaN"])).is_err());
        assert!(Config::parse(args(&["--wake-min-rms", "inf"])).is_err());
    }

    /// A zero window would average nothing and fire on every frame; provisioning's own regex
    /// rejects it, but the flag is reachable directly.
    #[test]
    fn zero_wake_window_is_rejected() {
        assert!(Config::parse(args(&["--wake-window", "0"])).is_err());
        assert!(Config::parse(args(&["--trigger-level", "0"])).is_err());
    }

    #[test]
    fn music_restore_grace_defaults_and_flag_parses() {
        // The un-duck is held this long so a long reply's inter-segment Idle gaps don't flap the
        // music back up between segments; only a truly-finished reply restores.
        assert_eq!(Config::default().music_restore_grace_ms, 3000);
        let c = Config::parse(args(&["--music-restore-grace-ms", "5000"])).unwrap();
        assert_eq!(c.music_restore_grace_ms, 5000);
    }

    #[test]
    fn music_flags_parse_and_default_off() {
        let on = Config::parse(pico_args::Arguments::from_vec(vec![
            "--music-mixer".into(), "Music".into(),
            "--music-card".into(), "sndrpihifiberry".into(),
            "--duck-percent".into(), "15".into(),
        ]))
        .unwrap();
        assert_eq!(on.music_mixer.as_deref(), Some("Music"));
        assert_eq!(on.music_card.as_deref(), Some("sndrpihifiberry"));
        assert_eq!(on.duck_percent, 15);

        let off = Config::parse(pico_args::Arguments::from_vec(vec![])).unwrap();
        assert_eq!(off.music_mixer, None);
        assert_eq!(off.duck_percent, 20); // default
    }

    // A unit that overrides only --snd-command (all of provisioning does) must not end up with
    // alerts pointed at the compiled-in default device. Alerts follow the normal sink unless
    // explicitly given their own, so a voice-only satellite needs no new provisioning at all.
    #[test]
    fn alert_snd_command_defaults_to_the_snd_command() {
        let c = Config::default();
        assert_eq!(c.alert_snd_command, c.snd_command);

        let c = Config::parse(args(&["--snd-command", "aplay -D tts -r 22050"])).unwrap();
        assert_eq!(c.snd_command, "aplay -D tts -r 22050");
        assert_eq!(c.alert_snd_command, "aplay -D tts -r 22050");
    }

    #[test]
    fn alert_snd_command_flag_overrides_only_the_alert_sink() {
        let c = Config::parse(args(&[
            "--snd-command", "aplay -D tts -r 22050",
            "--alert-snd-command", "aplay -D alert -r 22050",
        ]))
        .unwrap();
        assert_eq!(c.snd_command, "aplay -D tts -r 22050");
        assert_eq!(c.alert_snd_command, "aplay -D alert -r 22050");
    }

    #[test]
    fn volume_flags_parse_and_default_off() {
        let off = Config::default();
        assert_eq!(off.volume_sink, None, "no sink configured = local volume control off");
        assert_eq!(off.volume_step, 10);

        let on = Config::parse(pico_args::Arguments::from_vec(vec![
            "--volume-sink".into(), "@DEFAULT_AUDIO_SINK@".into(),
            "--volume-step".into(), "5".into(),
        ]))
        .unwrap();
        assert_eq!(on.volume_sink.as_deref(), Some("@DEFAULT_AUDIO_SINK@"));
        assert_eq!(on.volume_step, 5);
    }

    /// A zero step would make every command a silent no-op that still beeps, which reads as
    /// broken hardware rather than as misconfiguration.
    #[test]
    fn zero_volume_step_is_rejected() {
        assert!(Config::parse(args(&["--volume-step", "0"])).is_err());
    }

    /// The voice-only shape: no PipeWire, so the master is an ALSA softvol driven with amixer.
    #[test]
    fn volume_mixer_flags_parse_and_default_off() {
        let off = Config::default();
        assert_eq!(off.volume_mixer, None);
        assert_eq!(off.volume_card, None);

        let on = Config::parse(args(&["--volume-mixer", "Master", "--volume-card", "sndrpihifiberry"])).unwrap();
        assert_eq!(on.volume_mixer.as_deref(), Some("Master"));
        assert_eq!(on.volume_card.as_deref(), Some("sndrpihifiberry"));
        assert_eq!(on.volume_sink, None);
    }

    /// The two flags name two different tools for the SAME master, so a unit carrying both is
    /// misconfigured however it is resolved: whichever one lost, the satellite would beep its
    /// confirmation and move a level nobody hears. Reject it where the mistake is, at parse time.
    #[test]
    fn a_sink_and_a_mixer_together_are_rejected() {
        assert!(Config::parse(args(&["--volume-sink", "@DEFAULT_AUDIO_SINK@", "--volume-mixer", "Master"])).is_err());
    }

    /// A card with no control to look up on it is the same silent no-op, one typo away.
    #[test]
    fn a_volume_card_without_a_mixer_is_rejected() {
        assert!(Config::parse(args(&["--volume-card", "sndrpihifiberry"])).is_err());
    }

    #[test]
    fn defaults_are_sane() {
        let c = Config::default();
        assert_eq!(c.listen, "0.0.0.0:10700");
        assert!(c.mic_command.contains("arecord"));
        assert!(c.mic_command.contains("plughw:CARD=Array,DEV=0"));
        assert!(c.mic_command.contains("-F 20000"), "mic must pin a 20 ms period (default 125 ms delays every sample)");
        assert!(c.snd_command.contains("aplay"));
        assert!(c.snd_command.contains("plughw:CARD=sndrpihifiberry,DEV=0"));
        assert!(c.snd_command.contains("--start-delay=100000"), "playback must start at ~100 ms queued, not a full buffer");
        assert!(c.snd_command.contains("-F 50000"), "playback period 50 ms so the first writei lands sooner");
        assert_eq!(c.detector.threshold, 0.5);
        assert!(c.wake_enabled);
        assert_eq!(c.button, ButtonConfig::None);
        assert_eq!(c.led, LedConfig::Auto);
        assert_eq!(c.preroll_ms, 1000);
        assert_eq!(c.preroll_chunks(), 13); // ceil(1000 / 80)
        assert_eq!(c.wake_preroll_ms, 240);
        assert_eq!(c.wake_preroll_chunks(), 3);
    }
}
