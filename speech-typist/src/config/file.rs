//! Where the config comes from, and what is written when there is none.
//!
//! A `config.toml` beside the executable wins, which is what keeps the single-file distribution
//! genuinely portable: copy the executable and its config to another machine and the settings
//! travel with them. Failing that it is the one under the current user's application-data
//! directory, and that is the one written on first run.

use std::path::{Path, PathBuf};

use super::Config;

pub const FILE_NAME: &str = "config.toml";

/// The config, where it came from, and whether it had to be written first.
#[derive(Clone, Debug)]
pub struct Loaded {
    pub config: Config,
    pub path: PathBuf,
    pub written: bool,
}

/// Every way loading can fail says which file and why, because the alternative is a program that
/// started with settings nobody chose.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConfigError {
    Unreadable { path: PathBuf, why: String },
    Malformed { path: PathBuf, why: String },
    Unusable { path: PathBuf, why: String },
    NotWritten { path: PathBuf, why: String },
}

impl std::fmt::Display for ConfigError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Unreadable { path, why } => write!(f, "could not read {}: {why}", path.display()),
            Self::Malformed { path, why } => write!(f, "{} is not valid TOML: {why}", path.display()),
            Self::Unusable { path, why } => write!(f, "{} cannot be used: {why}", path.display()),
            Self::NotWritten { path, why } => {
                write!(f, "could not write {}: {why}", path.display())
            }
        }
    }
}

impl std::error::Error for ConfigError {}

/// Reads whichever of the two files exists, preferring the one beside the executable, and writes
/// the profile one with commented defaults when neither does.
pub fn load(beside_exe: &Path, in_profile: &Path) -> Result<Loaded, ConfigError> {
    for path in [beside_exe, in_profile] {
        if path.exists() {
            return read(path).map(|config| Loaded {
                config,
                path: path.to_path_buf(),
                written: false,
            });
        }
    }

    if let Some(parent) = in_profile.parent() {
        std::fs::create_dir_all(parent).map_err(|e| ConfigError::NotWritten {
            path: in_profile.to_path_buf(),
            why: e.to_string(),
        })?;
    }
    std::fs::write(in_profile, DEFAULTS).map_err(|e| ConfigError::NotWritten {
        path: in_profile.to_path_buf(),
        why: e.to_string(),
    })?;
    Ok(Loaded { config: Config::default(), path: in_profile.to_path_buf(), written: true })
}

fn read(path: &Path) -> Result<Config, ConfigError> {
    let text = std::fs::read_to_string(path)
        .map_err(|e| ConfigError::Unreadable { path: path.to_path_buf(), why: e.to_string() })?;
    let config: Config = toml::from_str(&text)
        .map_err(|e| ConfigError::Malformed { path: path.to_path_buf(), why: e.to_string() })?;
    config
        .validate()
        .map_err(|why| ConfigError::Unusable { path: path.to_path_buf(), why })?;
    Ok(config)
}

/// The two places looked in, on the machine this is running on. Thin on purpose: everything
/// interesting about precedence is in [`load`], which takes both paths and needs no Windows.
pub fn locations() -> (PathBuf, PathBuf) {
    let beside_exe = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join(FILE_NAME)))
        .unwrap_or_else(|| PathBuf::from(FILE_NAME));
    let profile_dir = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
        .unwrap_or_else(|| PathBuf::from("."));
    (beside_exe, profile_dir.join("speech-typist").join(FILE_NAME))
}

/// What first run writes. Every key it names carries its default, so this file and
/// [`Config::default`] cannot disagree without a test noticing.
pub const DEFAULTS: &str = r#"# speech-typist — hold a key, talk, and the words appear in whatever window you were using.
#
# A config.toml beside the executable wins over this one, so the settings can travel on a stick
# with the executable. Every value below is the default; delete a line to keep the default.

[lemonade]
# The Lemonade host as seen from this desktop. The compose-internal "lemonade:13305" means
# nothing from here, so this names the host instead.
base_url = "http://ai370:13305/v1"
# Kept in agreement with STT_MODEL by hand: compose's &stt-model anchor keeps four sides in
# lockstep and cannot reach this file, which is outside compose. A mismatch is NOT an error —
# Lemonade lazily pulls whatever it was asked for — so the symptom is a slow first dictation
# rather than a failure.
model = "Whisper-Large-v3-Turbo"
# Per-segment. A request that outlives it is retried once and then that segment is dropped.
request_timeout_secs = 30
# Each request's prompt is the binding's vocabulary followed by what the previous segment turned
# out to say. whisper.cpp caps the prompt at 224 tokens and keeps the TAIL, which would eat the
# vocabulary, so the cap is applied here and it is the chained text that loses its oldest words.
# This is characters, deliberately under that token budget rather than tuned to it.
max_prompt_chars = 700

[audio]
# A case-insensitive fragment of the capture device's name. Empty means the system default, so
# one microphone needs no configuration. The tray lists the devices it can see.
device_name = ""

[audio.cues]
# The start cue plays after the device is open, so it means "speak now" rather than "key
# received". Turning cues off keeps the tray icon.
enabled = true

[detector]
# Where a dictation is cut into segments. These are i16 RMS amplitudes: raise them in a loud
# room, lower them for a quiet microphone.
speech_rms = 500.0
silence_rms = 300.0
# How long below silence_rms cuts a segment.
silence_cut_ms = 400
# Kept either side of a cut, so a leading plosive is not clipped off the next segment.
padding_ms = 200
# A monologue with no pause is force-cut at this age, keeping every segment inside whisper's
# 30 s window.
max_segment_ms = 20000
# The force cut lands at the quietest point in this much of the preceding audio, so the split
# falls in a gap between words rather than mid-syllable.
force_cut_search_ms = 1000

[gate]
# Whisper hallucinates on near-silence — a stock "Thank you.", subtitle credits — and without
# this the fan and the keyboard put words nobody said into your document. A signal that is
# absent or malformed means no signal and the transcript is typed anyway: a shortcoming in the
# response must never silently swallow words that were actually said.
max_no_speech_prob = 0.6
min_avg_logprob = -1.0

[injection]
# "keys" is synthetic Unicode key events, indistinguishable from typing. "clipboard-paste" is
# the escape hatch for applications that mishandle them; it restores the previous clipboard.
# Never chosen automatically per application.
method = "keys"
# A dictation whose key-up never arrives — remote desktop, fast user switching, a hook that lost
# its window — ends by itself after this long, so the microphone is never held indefinitely.
watchdog_secs = 120

# A binding is a key together with the language its transcripts are expected in and the
# vocabulary they should be spelled by. Several can be listed and all are live at once; there is
# no mode to be in. The key held decides the language and the vocabulary for that dictation.
#
# key is a Windows virtual-key code. 124 is F13, which no application uses — and which many
# keyboards cannot produce, so use the tray's "set binding" submenu to press the key you
# actually want and have it written here.
[[bindings]]
key = 124
language = "es"
vocabulary = ""
"#;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Binding, InjectionMethod};
    use crate::host::KeyCode;

    /// A directory that cleans itself up, so these tests need nothing but a filesystem.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new(name: &str) -> Self {
            let path = std::env::temp_dir().join(format!(
                "speech-typist-{name}-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn at(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }

        fn write(&self, name: &str, contents: &str) -> PathBuf {
            let path = self.at(name);
            std::fs::write(&path, contents).unwrap();
            path
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn the_config_beside_the_executable_wins_over_the_one_in_the_user_profile() {
        let scratch = Scratch::new("precedence");
        let beside = scratch.write("beside.toml", "[lemonade]\nmodel = \"on-the-stick\"\n");
        let profile = scratch.write("profile.toml", "[lemonade]\nmodel = \"installed\"\n");

        let loaded = load(&beside, &profile).unwrap();

        assert_eq!(loaded.config.lemonade.model, "on-the-stick");
        assert_eq!(loaded.path, beside);
        assert!(!loaded.written);
    }

    #[test]
    fn the_profile_config_is_used_when_nothing_sits_beside_the_executable() {
        let scratch = Scratch::new("profile-only");
        let profile = scratch.write("profile.toml", "[lemonade]\nmodel = \"installed\"\n");

        let loaded = load(&scratch.at("absent.toml"), &profile).unwrap();

        assert_eq!(loaded.config.lemonade.model, "installed");
        assert_eq!(loaded.path, profile);
    }

    #[test]
    fn with_neither_present_one_is_written_to_the_profile_and_the_program_still_starts() {
        let scratch = Scratch::new("first-run");
        let profile = scratch.at("made-up/dirs/config.toml");

        let loaded = load(&scratch.at("absent.toml"), &profile).unwrap();

        assert!(loaded.written);
        assert_eq!(loaded.config, Config::default());
        let written = std::fs::read_to_string(&profile).unwrap();
        assert!(written.contains("# speech-typist"), "the defaults are written commented");
    }

    #[test]
    fn what_first_run_writes_is_exactly_what_the_defaults_are() {
        // The file and Config::default() are two spellings of the same settings, and nothing but
        // this test stops one from drifting away from the other.
        let from_file: Config = toml::from_str(DEFAULTS).unwrap();

        assert_eq!(from_file, Config::default());
    }

    #[test]
    fn the_written_defaults_say_why_the_model_has_to_be_kept_in_step_by_hand() {
        assert!(DEFAULTS.contains("STT_MODEL"));
        assert!(DEFAULTS.contains("slow first dictation"));
        assert!(DEFAULTS.contains("&stt-model"));
    }

    #[test]
    fn the_base_url_default_names_the_lemonade_host_not_the_compose_internal_name() {
        let base_url = Config::default().lemonade.base_url;

        assert!(!base_url.contains("lemonade:"), "{base_url} means nothing from a desktop");
        assert!(base_url.starts_with("http://"));
    }

    #[test]
    fn the_base_url_ends_where_the_dotnet_client_ends_so_the_route_below_it_matches() {
        // The .NET side pins "http://lemonade:13305/v1" and appends /audio/transcriptions.
        // Lemonade also serves /api/v1 (health lives there), so the wrong one of the two parses
        // fine and 404s only when someone is mid-sentence.
        let base_url = Config::default().lemonade.base_url;

        assert!(base_url.ends_with("/v1"), "{base_url}");
        assert!(!base_url.contains("/api/v1"), "{base_url} is the health route, not the OpenAI one");
    }

    #[test]
    fn the_model_default_is_the_one_compose_warms_the_container_with() {
        // DockerCompose/docker-compose.yml: `x-stt-model: &stt-model ${STT_MODEL:-...}`.
        assert_eq!(Config::default().lemonade.model, "Whisper-Large-v3-Turbo");
    }

    #[test]
    fn a_malformed_config_is_reported_with_the_file_and_the_reason() {
        let scratch = Scratch::new("malformed");
        let beside = scratch.write("beside.toml", "[lemonade\nmodel = ");

        let error = load(&beside, &scratch.at("absent.toml")).unwrap_err();

        assert!(matches!(&error, ConfigError::Malformed { path, .. } if *path == beside));
        assert!(error.to_string().contains("beside.toml"), "{error}");
    }

    #[test]
    fn a_key_nobody_recognises_is_reported_rather_than_quietly_ignored() {
        // A typo in a threshold would otherwise leave the person with the default and no clue.
        let scratch = Scratch::new("unknown-key");
        let beside = scratch.write("beside.toml", "[gate]\nmax_no_speach_prob = 0.6\n");

        let error = load(&beside, &scratch.at("absent.toml")).unwrap_err();

        assert!(matches!(error, ConfigError::Malformed { .. }), "{error}");
    }

    #[test]
    fn a_config_with_no_bindings_is_unusable_rather_than_silently_deaf() {
        let scratch = Scratch::new("no-bindings");
        let beside = scratch.write("beside.toml", "bindings = []\n");

        let error = load(&beside, &scratch.at("absent.toml")).unwrap_err();

        assert!(matches!(error, ConfigError::Unusable { .. }), "{error}");
    }

    #[test]
    fn two_bindings_on_one_key_are_unusable_because_neither_can_win() {
        let scratch = Scratch::new("clashing-bindings");
        let beside = scratch.write(
            "beside.toml",
            "[[bindings]]\nkey = 124\nlanguage = \"es\"\n\n[[bindings]]\nkey = 124\nlanguage = \"en\"\n",
        );

        let error = load(&beside, &scratch.at("absent.toml")).unwrap_err();

        assert!(matches!(error, ConfigError::Unusable { .. }), "{error}");
    }

    #[test]
    fn a_partial_config_keeps_the_defaults_for_everything_it_does_not_mention() {
        let scratch = Scratch::new("partial");
        let beside = scratch.write("beside.toml", "[lemonade]\nmodel = \"Whisper-Large-V3\"\n");

        let loaded = load(&beside, &scratch.at("absent.toml")).unwrap();

        assert_eq!(loaded.config.lemonade.model, "Whisper-Large-V3");
        assert_eq!(loaded.config.lemonade.request_timeout_secs, 30);
        assert_eq!(loaded.config.gate, crate::config::GateConfig::default());
        assert_eq!(loaded.config.bindings, vec![Binding::default()]);
    }

    #[test]
    fn bindings_carry_their_own_key_language_and_vocabulary() {
        let scratch = Scratch::new("bindings");
        let beside = scratch.write(
            "beside.toml",
            r#"
[[bindings]]
key = 124
language = "es"
vocabulary = "Ziggurat, Lemonade"

[[bindings]]
key = 125
language = "en"
vocabulary = "nabu, WASAPI"
"#,
        );

        let config = load(&beside, &scratch.at("absent.toml")).unwrap().config;

        assert_eq!(config.bindings.len(), 2);
        assert_eq!(config.binding_for(KeyCode(125)).map(|i| &config.bindings[i].language), Some(&"en".to_string()));
        assert_eq!(config.bindings[0].vocabulary, "Ziggurat, Lemonade");
    }

    #[test]
    fn the_injection_method_is_spelled_the_way_a_person_would_write_it() {
        let scratch = Scratch::new("injection");
        let beside = scratch.write("beside.toml", "[injection]\nmethod = \"clipboard-paste\"\n");

        let config = load(&beside, &scratch.at("absent.toml")).unwrap().config;

        assert_eq!(config.injection.method, InjectionMethod::ClipboardPaste);
    }
}
