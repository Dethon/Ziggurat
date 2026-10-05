//! The daemon's life, driven by the unit over its stdin and stdout, one line each way:
//! the unit writes the configuration, the daemon mounts, gives up root and answers
//! `{"served": [...]}` with the names at the root; `revoke`, ahead of a kill, is answered `revoked`
//! once the bridge has been told; then `exit` commits what the command still holds and answers
//! `done`. The unit unmounts: the daemon gave up the right to. A unit that dies closes stdin, which
//! is an `exit`.

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
    /// The uid (and gid) the daemon becomes once it has mounted and bound its socket.
    pub run_as: u32,
    /// Where action helpers reach this daemon; absent, no action can run from a script.
    #[serde(default)]
    pub action_socket: Option<String>,
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
    let helper = std::env::current_exe()
        .map(|exe| exe.with_file_name("vfs-action"))
        .and_then(std::fs::read)
        .unwrap_or_default();
    let vfs = Arc::new(Vfs::new(bridge).with_helper(helper));
    // Asked once, while the unit is certainly this daemon's parent: asked again after the unit died
    // it would name whoever adopted the daemon, and every command in the container descends from that.
    let unit = unsafe { libc::getppid() };
    let fuse = Fuse { vfs: vfs.clone(), uid: config.uid, gid: config.gid, unit };
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
    let session = fuser::Session::new(fuse, &config.mountpoint, &options)?;
    let actions = config.action_socket.as_deref().map(bind_actions).transpose()?;

    // Root was for the mount and the socket. Everything after answers the command, so it runs as
    // nobody in particular — and before any thread exists, because capabilities, no-new-privs and
    // the syscall filter are per thread, and a thread that kept them could exec back to root.
    give_up_root(config.run_as)?;
    // The first word from the bridge is read as nobody too: root parses nothing the agent sends.
    let served = vfs.served().unwrap_or_default();
    let session = session.spawn()?;
    if let Some(listener) = actions {
        serve_actions(listener, vfs.clone(), unit);
    }

    say(&serde_json::to_string(&Ready { served }).expect("serializes"))?;
    for line in lines {
        match line?.trim() {
            "exit" => break,
            "revoke" => {
                vfs.revoke();
                say("revoked")?;
            }
            _ => {}
        }
    }
    // Whatever the command still holds commits before the mount goes and before the unit answers,
    // so the agent's change log is whole when exec returns.
    vfs.finish();
    // Not dropped: dropping unmounts, which needs the root this daemon gave up (fuser would then
    // try a setuid fusermount the image does not have). Exiting closes the device, and the unit
    // unmounts.
    std::mem::forget(session);
    say("done")
}

fn give_up_root(uid: u32) -> io::Result<()> {
    let threads = std::fs::read_dir("/proc/self/task")?.count();
    if threads != 1 {
        return Err(io::Error::other(format!("vfs-daemon: {threads} threads before giving up root")));
    }
    let identity = crate::privilege::Identity { uid, gid: uid, groups: vec![], cwd: Some(c"/".into()), umask: 0o077 };
    unsafe { crate::privilege::become_identity(&identity) }
}

fn say(line: &str) -> io::Result<()> {
    let mut out = io::stdout().lock();
    writeln!(out, "{line}")?;
    out.flush()
}

/// Action helpers connect here, one connection per run. Bound as root, since the socket's directory
/// is this exec's own root-owned tmpfs; served once the daemon is nobody.
fn bind_actions(socket: &str) -> io::Result<std::os::unix::net::UnixListener> {
    use std::os::unix::fs::PermissionsExt;

    let _ = std::fs::remove_file(socket);
    let listener = std::os::unix::net::UnixListener::bind(socket)?;
    // The helper runs as PUID.
    std::fs::set_permissions(socket, std::fs::Permissions::from_mode(0o666))?;
    Ok(listener)
}

/// Only a process of this exec — a descendant of the unit that started this daemon — is answered.
fn serve_actions<B: Bridge>(listener: std::os::unix::net::UnixListener, vfs: Arc<Vfs<B>>, unit: i32) {
    std::thread::spawn(move || {
        listener.incoming().filter_map(Result::ok).for_each(|connection| {
            let vfs = vfs.clone();
            std::thread::spawn(move || answer_action(connection, &vfs, unit));
        });
    });
}

fn answer_action<B: Bridge>(connection: std::os::unix::net::UnixStream, vfs: &Vfs<B>, unit: i32) {
    use super::actions::{request_line, ActionReply, ActionRequest};

    if !of_this_exec(&connection, unit) {
        return;
    }
    let line = request_line(&connection);
    if line.is_empty() {
        return;
    }
    let reply = match serde_json::from_str::<ActionRequest>(&line) {
        Ok(request) => match vfs.action(&request.path, &request.argv) {
            Ok(output) => ActionReply::Ran(output),
            Err(errno) => ActionReply::Refused { errno },
        },
        Err(_) => ActionReply::Refused { errno: libc::EINVAL },
    };
    let mut writer = &connection;
    let _ = writeln!(writer, "{}", serde_json::to_string(&reply).expect("serializes"));
}

// The peer is a process of this exec: a descendant of the unit that started this daemon, which is
// a child subreaper, so everything the command starts stays under it. Asked of /proc/<pid>/stat,
// which anyone may read — the peer's namespace link would need CAP_SYS_PTRACE, which the container
// does not have. The socket living in the exec's own tmpfs is the other half of the check.
fn of_this_exec(connection: &std::os::unix::net::UnixStream, unit: i32) -> bool {
    crate::proctree::peer(connection)
        .is_some_and(|peer| crate::proctree::descends_from(peer.pid, unit, crate::proctree::parent_of))
}
