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

pub const MOUNTPOINT: &str = "/vfs";

/// Mounting is a few syscalls and one listing; this only bounds a bridge that never answers.
const READY_WITHIN: Duration = Duration::from_secs(20);

/// The final commits of a command's held files go through the bridge before the daemon answers.
const DONE_WITHIN: Duration = Duration::from_secs(120);

pub struct Served {
    child: Child,
    stdin: ChildStdin,
    lines: Receiver<String>,
    /// Names served at /vfs/<name> only, because the image already has /<name>.
    pub vfs_only: Vec<String>,
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

        let config = serde_json::json!({
            "bridgeUrl": grant.url,
            "token": grant.token,
            "uid": uid,
            "gid": gid,
            "mountpoint": MOUNTPOINT,
            "runAs": daemon_uid,
            "actionSocket": crate::vfs::actions::SOCKET,
        });
        writeln!(stdin, "{config}")?;
        stdin.flush()?;

        let ready = lines.recv_timeout(READY_WITHIN).map_err(|_| {
            let _ = child.kill();
            io::Error::other("the vfs daemon did not mount")
        })?;
        let served: Vec<String> = serde_json::from_str::<serde_json::Value>(&ready)
            .ok()
            .and_then(|v| v["served"].as_array().cloned())
            .unwrap_or_default()
            .iter()
            .filter_map(|n| n.as_str().map(String::from))
            .collect();
        let vfs_only = served.iter().filter(|name| !link(name)).cloned().collect();

        Ok(Served { child, stdin, lines, vfs_only })
    }

    pub fn pid(&self) -> i32 {
        self.child.id() as i32
    }

    /// Before a kill, never after: killing closes the command's files and the kernel's releases
    /// would commit before a later revocation landed. Waits for the daemon to say the bridge has it.
    pub fn revoke(&mut self) {
        let _ = writeln!(self.stdin, "revoke");
        let _ = self.stdin.flush();
        let _ = self.lines.recv_timeout(READY_WITHIN);
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
