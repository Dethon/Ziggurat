//! The daemon's life, driven by the unit over its stdin and stdout, one line each way:
//! the unit writes the configuration, the daemon mounts and answers `ready` with the served names;
//! then `exit` commits what the command still holds, unmounts and answers `done`. A unit that dies closes stdin, which is an `exit`.

use std::io::{self, BufRead, Write};
use std::sync::Arc;

use serde::{Deserialize, Serialize};

use super::bridge::Bridge;
use super::core::Vfs;
use super::fuse::Fuse;
use super::http::HttpBridge;

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DaemonConfig {
    pub bridge_url: String,
    pub token: String,
    pub uid: u32,
    pub gid: u32,
    pub mountpoint: String,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Ready {
    pub served: Vec<String>,
}

pub fn run() -> io::Result<()> {
    let stdin = io::stdin();
    let mut lines = stdin.lock().lines();
    let config: DaemonConfig = serde_json::from_str(&lines.next().transpose()?.unwrap_or_default())
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidInput, e))?;

    let bridge = HttpBridge::new(&config.bridge_url, &config.token);
    let served: Vec<String> = bridge
        .list("/")
        .map(|l| l.entries.into_iter().map(|e| e.name).collect())
        .unwrap_or_default();

    let vfs = Arc::new(Vfs::new(bridge));
    let fuse = Fuse { vfs: vfs.clone(), uid: config.uid, gid: config.gid };
    let mut options = fuser::Config::default();
    options.mount_options = vec![
        fuser::MountOption::FSName("ziggurat-vfs".into()),
        fuser::MountOption::DefaultPermissions,
        fuser::MountOption::NoSuid,
        fuser::MountOption::NoDev,
        fuser::MountOption::Exec,
    ];
    // allow_other: the daemon mounts as root, and the command reading through it is PUID.
    options.acl = fuser::SessionACL::All;
    let session = fuser::spawn_mount(fuse, &config.mountpoint, &options)?;

    say(&serde_json::to_string(&Ready { served }).expect("serializes"))?;
    for line in lines {
        if line?.trim() == "exit" {
            break;
        }
    }
    // Whatever the command still holds commits before the mount goes and before the unit answers,
    // so the agent's change log is whole when exec returns.
    vfs.finish();
    drop(session);
    say("done")
}

fn say(line: &str) -> io::Result<()> {
    let mut out = io::stdout().lock();
    writeln!(out, "{line}")?;
    out.flush()
}
