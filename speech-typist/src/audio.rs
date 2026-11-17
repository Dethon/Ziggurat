//! Which microphone the dictation is captured through.

/// Which of the capture devices the config asked for. `None` means the system default, so one
/// microphone needs no configuration at all.
///
/// A named device that is not present is an error rather than a silent fall back to whatever is:
/// the whole reason to name one is that plugging in a webcam must not quietly change which
/// microphone a person dictates through, and falling back would do exactly that.
pub fn choose_device(available: &[String], fragment: &str) -> Result<Option<usize>, String> {
    let fragment = fragment.trim();
    if fragment.is_empty() {
        return Ok(None);
    }
    let wanted = fragment.to_lowercase();
    available
        .iter()
        .position(|name| name.to_lowercase().contains(&wanted))
        .map(Some)
        .ok_or_else(|| {
            format!(
                "no capture device matches \"{fragment}\". The ones present are: {}",
                if available.is_empty() { "none".to_string() } else { available.join(", ") }
            )
        })
}

/// The stream kept between dictations: set up, and stopped.
///
/// Setting a stream up is what costs the time — about 350 ms on a wireless headset, measured on
/// the Arctis Nova Pro Wireless, against under 20 ms to start one that already is. So one is
/// prepared ahead of each dictation and only started when a binding goes down.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Prepared<'a> {
    /// The name of the device it was set up on.
    pub device: &'a str,
    /// It reported an error since — typically the device going away under it.
    pub failed: bool,
}

/// What a key-down does to get audio flowing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Start {
    /// Start the prepared stream.
    Resume,
    /// Set a new one up, as the slow path.
    Build,
}

/// Whether the prepared stream can be started, given the device the dictation wants now. Asked
/// on every key-down rather than trusted, because the default microphone changes underneath a
/// stream that was prepared on another — a remote desktop session connecting, a headset switched
/// off — and a stream on the wrong device records the wrong room, or nothing.
pub fn how_to_start(prepared: Option<Prepared<'_>>, wanted: &str) -> Start {
    match prepared {
        Some(held) if !held.failed && held.device == wanted => Start::Resume,
        _ => Start::Build,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_prepared_stream_on_the_microphone_wanted_now_is_only_started() {
        let held = Prepared { device: "Microphone (Arctis Nova Pro Wireless)", failed: false };
        assert_eq!(how_to_start(Some(held), "Microphone (Arctis Nova Pro Wireless)"), Start::Resume);
    }

    #[test]
    fn with_nothing_prepared_the_stream_is_built() {
        assert_eq!(how_to_start(None, "Remote Audio"), Start::Build);
    }

    #[test]
    fn a_stream_prepared_on_another_microphone_is_built_again() {
        // A remote desktop session connecting swaps the default microphone underneath a stream
        // that was prepared on the old one, and a headset that went away leaves one behind.
        let held = Prepared { device: "Microphone (Arctis Nova Pro Wireless)", failed: false };
        assert_eq!(how_to_start(Some(held), "Remote Audio"), Start::Build);
    }

    #[test]
    fn a_stream_that_reported_an_error_is_built_again() {
        let held = Prepared { device: "Remote Audio", failed: true };
        assert_eq!(how_to_start(Some(held), "Remote Audio"), Start::Build);
    }

    fn devices() -> Vec<String> {
        vec![
            "Microphone Array (Realtek(R) Audio)".into(),
            "Headset (Jabra Evolve2 65)".into(),
            "Microphone (HD Pro Webcam C920)".into(),
        ]
    }

    #[test]
    fn with_nothing_configured_the_system_default_is_used() {
        assert_eq!(choose_device(&devices(), ""), Ok(None));
        assert_eq!(choose_device(&devices(), "   "), Ok(None));
    }

    #[test]
    fn a_fragment_of_the_name_is_enough_and_the_case_does_not_matter() {
        assert_eq!(choose_device(&devices(), "jabra"), Ok(Some(1)));
        assert_eq!(choose_device(&devices(), "JABRA EVOLVE"), Ok(Some(1)));
    }

    #[test]
    fn a_device_that_is_not_present_is_reported_with_the_ones_that_are() {
        // Silently falling back would be the failure this key exists to prevent: plugging in a
        // webcam must not quietly change which microphone the dictation goes through.
        let error = choose_device(&devices(), "Yeti").unwrap_err();

        assert!(error.contains("Yeti"), "{error}");
        assert!(error.contains("Jabra Evolve2 65"), "{error}");
    }

    #[test]
    fn a_fragment_matching_several_takes_the_first_the_system_listed() {
        // "Microphone" matches two here. The first is what Windows enumerated first, which is
        // stable enough to be useful and is why the tray lists the full names.
        assert_eq!(choose_device(&devices(), "Microphone"), Ok(Some(0)));
    }

    #[test]
    fn with_no_devices_at_all_a_named_one_is_still_an_error_rather_than_the_default() {
        assert!(choose_device(&[], "Jabra").is_err());
        assert_eq!(choose_device(&[], ""), Ok(None));
    }
}
