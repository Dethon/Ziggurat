pub mod capture;
pub mod playback;
pub mod cues;
pub mod room;

use tokio::process::Command;

/// Build the Command for a mic/snd command line. Plain argv command lines (the default
/// arecord/aplay invocations) exec directly: that drops the `sh -c` exec layer from every
/// player start (~2-10 ms on a Pi between "first TTS/cue bytes ready" and sound) and — more
/// importantly — makes kill_on_drop/kill() deliver SIGKILL to aplay/arecord themselves rather
/// than their sh parent, so the exclusive ALSA device is released immediately on preempt and
/// connection supersede. Anything shell-shaped (pipes, redirects, quotes, env-assignment
/// prefixes — e.g. the WSL mic gain pipe or `cat >/dev/null` test sinks) keeps the sh path.
pub(crate) fn build_command(cmdline: &str) -> Command {
    match plain_argv(cmdline) {
        Some(argv) => {
            let mut c = Command::new(argv[0]);
            c.args(&argv[1..]);
            c
        }
        None => {
            let mut c = Command::new("sh");
            c.arg("-c").arg(cmdline);
            c
        }
    }
}

/// Fire-and-forget a command from a context that cannot await — the `Drop` fail-safes that put a
/// mixer back when a connection dies. Returns the child's pid, or `None` when it never started.
///
/// The child is waited on from a throwaway thread. Dropping a `std::process::Child` neither kills
/// nor reaps it, so without that every call leaves a zombie until the satellite exits (a dozen
/// defunct `amixer`s after twelve days on the office unit, one per hub reconnect). A thread
/// rather than a task because the callers are `Drop`s, which also run while the runtime is going
/// away; if the thread cannot be started the command still runs and is merely left unreaped.
pub(crate) fn run_detached(mut cmd: std::process::Command) -> Option<u32> {
    let mut child = cmd
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .ok()?;
    let pid = child.id();
    let _ = std::thread::Builder::new().name("reap-detached".into()).spawn(move || {
        let _ = child.wait();
    });
    Some(pid)
}

/// A command line qualifies as plain argv when it has no sh metacharacters anywhere and no
/// `=` in the program word (an env-assignment prefix like `LANG=C aplay …` is valid sh but
/// would mis-exec as argv[0] under naive splitting). `=` in later tokens is fine
/// (`--start-delay=100000`).
fn plain_argv(cmdline: &str) -> Option<Vec<&str>> {
    const META: &[char] = &[
        '|', '&', ';', '<', '>', '(', ')', '$', '`', '"', '\'', '\\', '*', '?', '[', ']',
        '#', '~', '!', '{', '}',
    ];
    let argv: Vec<&str> = cmdline.split_whitespace().collect();
    let program = argv.first()?;
    if program.contains('=') || cmdline.contains(META) {
        return None;
    }
    Some(argv)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    #[test]
    fn default_audio_commands_bypass_the_shell() {
        let cfg = Config::default();
        assert!(plain_argv(&cfg.mic_command).is_some(), "default arecord must exec directly");
        assert!(plain_argv(&cfg.snd_command).is_some(), "default aplay must exec directly");
    }

    #[test]
    fn shell_shaped_commands_keep_the_sh_path() {
        assert!(plain_argv("cat >/dev/null").is_none());
        assert!(plain_argv("arecord -D x -t raw | python3 -u -c \"gain\"").is_none());
        assert!(plain_argv("LANG=C aplay -D plughw:0,0").is_none(), "env prefix is not plain argv");
        assert!(plain_argv("").is_none());
    }

    /// Nobody waits on a detached command by definition, so the helper has to: a child that is
    /// spawned and dropped stays a zombie for the life of the process, and the satellite fires
    /// one of these at every connection teardown. A reaped pid leaves /proc; a zombie does not.
    #[test]
    fn a_detached_command_is_reaped_once_it_exits() {
        let pid = run_detached(std::process::Command::new("true")).expect("`true` must spawn");
        let proc_entry = std::path::PathBuf::from(format!("/proc/{pid}"));
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        while proc_entry.exists() && std::time::Instant::now() < deadline {
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        assert!(!proc_entry.exists(), "pid {pid} exited but was never waited on");
    }

    #[test]
    fn a_detached_command_that_cannot_start_reports_nothing() {
        assert_eq!(run_detached(std::process::Command::new("/nonexistent/mixer")), None);
    }

    #[tokio::test]
    async fn direct_exec_spawns_and_runs() {
        let out = build_command("head -c 4 /dev/zero")
            .stdout(std::process::Stdio::piped())
            .spawn()
            .unwrap()
            .wait_with_output()
            .await
            .unwrap();
        assert_eq!(out.stdout.len(), 4);
    }
}
