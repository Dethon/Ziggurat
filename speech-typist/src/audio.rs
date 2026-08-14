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

#[cfg(test)]
mod tests {
    use super::*;

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
