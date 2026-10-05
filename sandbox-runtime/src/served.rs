//! The unit's side of the call's FUSE daemon: start it inside the exec's namespace with the call
//! token over its stdin, wait for it to mount, and put a link at /<name> for each served mount.
//!
//! Links, not bind mounts: a rename between two mounts must arrive at the daemon as one rename so
//! the bridge can make it a transfer, and bind mounts make it cross-device, which `mv` silently
//! turns into copy-and-unlink (spike finding 2). The links live in the container's own root, which
//! every namespace shares, so they outlast the exec and dangle — ENOENT — wherever the mount is not
//! served; a name whose path the image already uses is served only at /vfs/<name>.

use std::ffi::CString;
use std::io::{self, BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::time::Duration;

use crate::protocol::BridgeGrant;
use crate::vfs::daemon::{DaemonConfig, Ready};
use crate::vfs::MOUNTPOINT;

/// Mounting is a few syscalls and one listing; this only bounds a bridge that never answers.
const READY_WITHIN: Duration = Duration::from_secs(20);

/// One request to the bridge, bounded by the daemon itself (`HttpBridge::revoke`); this is the
/// margin past it.
const REVOKED_WITHIN: Duration = Duration::from_secs(15);

/// The final commits of a command's held files go through the bridge before the daemon answers.
const DONE_WITHIN: Duration = Duration::from_secs(120);

pub struct Served {
    child: Child,
    stdin: ChildStdin,
    lines: Receiver<String>,
}

impl Served {
    pub fn start(grant: &BridgeGrant, uid: u32, gid: u32, daemon_uid: u32) -> io::Result<Served> {
        unsafe {
            let target = CString::new(MOUNTPOINT).unwrap();
            // The namespace inherited the container's view, which may hold another exec's mount.
            libc::umount2(target.as_ptr(), libc::MNT_DETACH);
        }
        std::fs::create_dir_all(MOUNTPOINT)?;
        own_tmpfs(crate::vfs::actions::SOCKET_DIR)?;

        let daemon = std::env::current_exe()?.with_file_name("vfs-daemon");
        let mut child = Command::new(daemon)
            .env_clear()
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?;
        let mut stdin = child.stdin.take().expect("piped");
        let stdout = child.stdout.take().expect("piped");
        let (sender, lines) = mpsc::channel();
        std::thread::spawn(move || {
            BufReader::new(stdout).lines().map_while(Result::ok).for_each(|line| {
                let _ = sender.send(line);
            });
        });

        let config = DaemonConfig {
            bridge_url: grant.url.clone(),
            token: grant.token.clone(),
            uid,
            gid,
            mountpoint: MOUNTPOINT.into(),
            run_as: daemon_uid,
            action_socket: Some(crate::vfs::actions::SOCKET.into()),
        };
        writeln!(stdin, "{}", serde_json::to_string(&config).expect("serializes"))?;
        stdin.flush()?;

        let ready = lines.recv_timeout(READY_WITHIN).map_err(|_| {
            let _ = child.kill();
            io::Error::other("the vfs daemon did not mount")
        })?;
        // A name the image already uses at its root gets no link, and is served at /vfs/<name> only.
        serde_json::from_str::<Ready>(&ready).map(|r| r.served).unwrap_or_default().iter().for_each(|name| {
            link(name);
        });

        Ok(Served { child, stdin, lines })
    }

    pub fn pid(&self) -> i32 {
        self.child.id() as i32
    }

    /// Before a kill, never after: killing closes the command's files and the kernel's releases
    /// would commit before a later revocation landed. Waits for the daemon to say it is revoked —
    /// told to the bridge, or sealed where the bridge could not be told. A daemon that says nothing
    /// in time is killed first: with it gone the mount answers nothing, so nothing the command's
    /// death flushes can reach the agent.
    pub fn revoke(&mut self) {
        let _ = writeln!(self.stdin, "revoke");
        let _ = self.stdin.flush();
        if self.lines.recv_timeout(REVOKED_WITHIN).is_err() {
            let _ = self.child.kill();
        }
    }

    /// The command is over: the daemon commits what it still holds and exits, and the mount goes
    /// with it. Unmounting is the unit's, because the daemon gave up the right to once it mounted.
    pub fn finish(mut self) {
        let _ = writeln!(self.stdin, "exit");
        let _ = self.stdin.flush();
        let _ = self.lines.recv_timeout(DONE_WITHIN);
        drop(self.stdin);
        let _ = self.child.wait();
        let target = CString::new(MOUNTPOINT).unwrap();
        unsafe {
            libc::umount2(target.as_ptr(), libc::MNT_DETACH);
        }
    }
}

/// A tmpfs of this namespace's own at `dir`: the action socket lives there, so a helper in another
/// exec cannot even find this exec's daemon.
fn own_tmpfs(dir: &str) -> io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let target = CString::new(dir).unwrap();
    let fstype = CString::new("tmpfs").unwrap();
    let options = CString::new("mode=0755").unwrap();
    let mounted = unsafe {
        libc::mount(
            fstype.as_ptr(),
            target.as_ptr(),
            fstype.as_ptr(),
            libc::MS_NOSUID | libc::MS_NODEV | libc::MS_NOEXEC,
            options.as_ptr() as *const libc::c_void,
        )
    };
    if mounted == -1 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

/// True when /<name> is (now) the link into /vfs. False for a name that would leave the root,
/// `sandbox` (the image's own alias of the root), or a path the image already uses.
pub fn link(name: &str) -> bool {
    if name.is_empty() || name.contains('/') || name == "." || name == ".." || name == "sandbox" {
        return false;
    }
    let path = format!("/{name}");
    let target = format!("{MOUNTPOINT}/{name}");
    match std::fs::symlink_metadata(&path) {
        Ok(meta) => meta.file_type().is_symlink() && std::fs::read_link(&path).is_ok_and(|t| t == Path::new(&target)),
        Err(e) if e.kind() == io::ErrorKind::NotFound => {
            std::os::unix::fs::symlink(&target, &path).is_ok()
                // Another exec may have made the same link a moment ago.
                || std::fs::read_link(&path).is_ok_and(|t| t == Path::new(&target))
        }
        Err(_) => false,
    }
}
