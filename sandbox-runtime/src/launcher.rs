//! The container's entrypoint, as root: share the home volume between the two uids, open the
//! socket only the server's uid may use, start the server as that uid, and hand every connection
//! to a unit of its own. It parses nothing the agent sends beyond one JSON line per exec, which is
//! the point of keeping it separate from the server that parses everything.

use std::ffi::CString;
use std::fs;
use std::io;
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::{UnixListener, UnixStream};
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicI32, Ordering};

use crate::privilege::{become_identity, kept_groups, Identity};

pub struct LauncherConfig {
    pub puid: u32,
    pub pgid: u32,
    pub server_uid: u32,
    /// What each exec's vfs daemon becomes once it has mounted: neither root nor PUID.
    pub daemon_uid: u32,
    pub home: String,
    pub socket: String,
    pub server_command: Vec<String>,
}

static SERVER_PID: AtomicI32 = AtomicI32::new(0);

extern "C" fn forward(signal: libc::c_int) {
    let pid = SERVER_PID.load(Ordering::SeqCst);
    if pid > 0 {
        unsafe {
            libc::kill(pid, signal);
        }
    }
}

pub fn serve(config: LauncherConfig) -> io::Result<i32> {
    if config.server_command.is_empty() {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "no server command after --"));
    }

    crate::home::share(Path::new(&config.home), config.puid, config.pgid);
    let groups = kept_groups(config.pgid)?;
    let listener = open_socket(&config)?;

    let server = Identity {
        uid: config.server_uid,
        gid: config.pgid,
        groups: vec![config.pgid],
        cwd: None,
        umask: 0o002,
    };
    let mut command = Command::new(&config.server_command[0]);
    command.args(&config.server_command[1..]).env("LAUNCHERSOCKET", &config.socket);
    unsafe {
        command.pre_exec(move || become_identity(&server));
    }
    let mut child = command.spawn()?;
    SERVER_PID.store(child.id() as i32, Ordering::SeqCst);
    unsafe {
        libc::signal(libc::SIGTERM, forward as *const () as libc::sighandler_t);
        libc::signal(libc::SIGINT, forward as *const () as libc::sighandler_t);
    }

    let unit_env = [
        ("PUID", config.puid.to_string()),
        ("PGID", config.pgid.to_string()),
        ("SANDBOX_SERVER_UID", config.server_uid.to_string()),
        ("SANDBOX_DAEMON_UID", config.daemon_uid.to_string()),
        ("SANDBOX_HOME", config.home.clone()),
        (
            "SANDBOX_COMMAND_GROUPS",
            groups.iter().map(|g| g.to_string()).collect::<Vec<_>>().join(","),
        ),
    ];
    let server_uid = config.server_uid;
    std::thread::spawn(move || {
        listener
            .incoming()
            .filter_map(Result::ok)
            // Checked as well as the socket's mode: the mode is what a mistake in the image would undo.
            .filter(|connection| crate::proctree::peer(connection).is_some_and(|peer| peer.uid == server_uid))
            .for_each(|connection| {
                if let Err(e) = start_unit(connection, &unit_env) {
                    eprintln!("sandbox-launcher: cannot start a unit: {e}");
                }
            });
    });

    let status = child.wait()?;
    Ok(status.code().unwrap_or(1))
}

// The directory is root's, the socket the server uid's alone: a command runs as PUID and must not
// be able to ask for a command of its own, which would run outside its own timeout and namespace.
fn open_socket(config: &LauncherConfig) -> io::Result<UnixListener> {
    let path = Path::new(&config.socket);
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir)?;
        fs::set_permissions(dir, fs::Permissions::from_mode(0o755))?;
    }
    let _ = fs::remove_file(path);
    let listener = UnixListener::bind(path)?;
    let c_path = CString::new(config.socket.clone())?;
    if unsafe { libc::chown(c_path.as_ptr(), config.server_uid, config.pgid) } == -1 {
        return Err(io::Error::last_os_error());
    }
    fs::set_permissions(path, fs::Permissions::from_mode(0o600))?;
    Ok(listener)
}

fn start_unit(connection: UnixStream, env: &[(&str, String)]) -> io::Result<()> {
    let reader = connection.try_clone()?;
    let mut unit = Command::new(std::env::current_exe()?)
        .arg("unit")
        .env_clear()
        .envs(env.iter().map(|(k, v)| (*k, v.as_str())))
        .stdin(Stdio::from(std::os::fd::OwnedFd::from(reader)))
        .stdout(Stdio::from(std::os::fd::OwnedFd::from(connection)))
        .stderr(Stdio::inherit())
        .spawn()?;
    std::thread::spawn(move || unit.wait());
    Ok(())
}
