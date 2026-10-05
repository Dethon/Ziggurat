//! The action helper's protocol with the call's daemon. The helper runs as PUID and holds no token:
//! it hands the daemon its own virtual path and its arguments over a socket that exists
//! only inside this exec's namespace, and the daemon — which checks the caller is a process of this
//! exec — commits what the script holds and asks the bridge to run the action.

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
    fn a_reply_round_trips() {
        let ran = ActionReply::Ran(ActionOutput { stdout: "ok\n".into(), stderr: String::new(), exit_code: 3 });
        let refused = ActionReply::Refused { errno: libc::EACCES };

        for reply in [ran, refused] {
            let line = serde_json::to_string(&reply).unwrap();
            assert_eq!(serde_json::from_str::<ActionReply>(&line).unwrap(), reply);
        }
    }
}
