//! The action helper's protocol with the call's daemon. The helper runs as PUID and holds no token:
//! it hands the daemon its own virtual path and its arguments over a socket that exists
//! only inside this exec's namespace, and the daemon — which checks the caller is a process of this
//! exec — commits what the script holds and asks the bridge to run the action.

use std::ffi::OsString;
use std::io::{BufRead, BufReader, Read};

use serde::{Deserialize, Serialize};

use super::bridge::ActionOutput;
use super::MOUNTPOINT;

/// Inside each exec's private tmpfs, so another exec's helper cannot even find it.
pub const SOCKET: &str = "/run/vfs/action.sock";
pub const SOCKET_DIR: &str = "/run/vfs";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActionRequest {
    pub path: String,
    pub argv: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ActionReply {
    Ran(ActionOutput),
    Refused { errno: i32 },
}

/// The longest request the daemon reads: a command line as long as the kernel lets one be, JSON
/// escaping included. The helper is the command's to run and a connection is the command's to
/// open, so a line with no end must not be one the daemon keeps allocating for.
pub const MAX_REQUEST: u64 = 16 << 20;

/// One line off the socket, or as much of one as fits; a request cut short does not parse.
pub fn request_line(from: impl Read) -> String {
    let mut line = Vec::new();
    let _ = BufReader::new(from).take(MAX_REQUEST).read_until(b'\n', &mut line);
    String::from_utf8_lossy(&line).into_owned()
}

/// The action's arguments as the words the command gave. None where one is not text: the request
/// is JSON, and an argument rewritten on the way would be a different argument.
pub fn arguments(args: impl IntoIterator<Item = OsString>) -> Option<Vec<String>> {
    args.into_iter().map(|argument| argument.into_string().ok()).collect()
}

/// The action an executable is, from the path the kernel ran it by: `/vfs/timers/dismiss` is the
/// timers' `dismiss`. Anything outside /vfs is the helper run as itself, which is no action.
pub fn virtual_path(exe: &str) -> Option<String> {
    let rest = exe.strip_prefix(MOUNTPOINT)?;
    (rest.starts_with('/') && rest.len() > 1).then(|| rest.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_action_is_named_by_its_path_under_vfs() {
        assert_eq!(virtual_path("/vfs/timers/dismiss").as_deref(), Some("/timers/dismiss"));
        assert_eq!(
            virtual_path("/vfs/schedules/jonas/daily/run_now").as_deref(),
            Some("/schedules/jonas/daily/run_now")
        );
    }

    #[test]
    fn the_helper_run_as_itself_is_no_action() {
        assert_eq!(virtual_path("/usr/local/bin/vfs-action"), None);
        assert_eq!(virtual_path("/vfs"), None);
        assert_eq!(virtual_path("/vfsx/timers/dismiss"), None);
    }

    #[test]
    fn an_argument_that_is_not_text_is_no_request() {
        use std::os::unix::ffi::OsStringExt;

        let words = [OsString::from("--name"), OsString::from("café")];
        let bytes = [OsString::from("--name"), OsString::from_vec(vec![b'a', 0xff])];

        assert_eq!(arguments(words).unwrap(), ["--name", "café"]);
        assert_eq!(arguments(bytes), None);
    }

    #[test]
    fn a_request_line_is_read_up_to_its_bound_and_no_further() {
        let endless = std::io::repeat(b'a');

        assert_eq!(request_line(endless).len() as u64, MAX_REQUEST);
        assert_eq!(request_line(&b"{\"path\":\"/timers/dismiss\",\"argv\":[]}\nrest"[..]), "{\"path\":\"/timers/dismiss\",\"argv\":[]}\n");
    }

    #[test]
    fn a_reply_round_trips() {
        let ran = ActionReply::Ran(ActionOutput { stdout: "ok\n".into(), stderr: String::new(), exit_code: 3 });
        let refused = ActionReply::Refused { errno: libc::EACCES };

        for reply in [ran, refused] {
            let line = serde_json::to_string(&reply).unwrap();
            assert_eq!(serde_json::from_str::<ActionReply>(&line).unwrap(), reply);
        }
    }
}
