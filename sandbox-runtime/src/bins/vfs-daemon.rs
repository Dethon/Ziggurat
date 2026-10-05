//! `vfs-daemon`: one exec's FUSE filesystem at /vfs, started by the launcher's unit as root inside
//! the exec's mount namespace, configured over stdin so the call token never appears in argv or
//! the environment.

#[cfg(target_os = "linux")]
fn main() {
    if let Err(e) = sandbox_runtime::vfs::daemon::run() {
        eprintln!("vfs-daemon: {e}");
        std::process::exit(1);
    }
}

#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("vfs-daemon runs on Linux only");
    std::process::exit(2);
}
