//! One exec, start to finish, in a process of its own. The launcher hands it the server's
//! connection as stdin and stdout and goes back to accepting: the request is read from it, the
//! answer written to it, and the server hanging up on it is the cancellation.
//!
//! A process of its own because the mount namespace has to be: `unshare(CLONE_NEWNS)` changes the
//! caller, and the launcher's accept loop must stay in the container's namespace.

use std::collections::HashMap;
use std::ffi::CString;
use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::net::UnixStream;
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::{Child, ChildStderr, ChildStdout, Command, Stdio};
use std::time::{Duration, Instant};

use crate::output::CappedOutput;
use crate::privilege::{become_identity, Identity};
use crate::proctree;
use crate::protocol::{ExecFailure, ExecRequest, ExecResponse};
use crate::served::Served;

pub struct UnitConfig {
    pub puid: u32,
    pub pgid: u32,
    pub server_uid: u32,
    pub home: String,
    pub groups: Vec<libc::gid_t>,
}

/// Once the tree is dead its pipes close; this only bounds a holder that somehow survived.
const DRAIN_AFTER_KILL: Duration = Duration::from_secs(2);

enum Ending {
    Finished,
    TimedOut,
    Cancelled,
}

pub fn run(config: &UnitConfig) -> i32 {
    // fd 0 and fd 1 are the same socket; one handle reads, the other writes.
    let connection = unsafe { UnixStream::from_raw_fd(0) };
    let mut answer = unsafe { UnixStream::from_raw_fd(1) };

    let mut line = String::new();
    if BufReader::new(&connection).read_line(&mut line).unwrap_or(0) == 0 {
        return 1;
    }
    let request: ExecRequest = match serde_json::from_str(&line) {
        Ok(request) => request,
        Err(e) => return fail(&mut answer, &format!("unreadable request: {e}")),
    };

    if let Err(e) = isolate() {
        return fail(&mut answer, &format!("cannot isolate the command: {e}"));
    }

    crate::home::adopt(Path::new(&config.home), config.server_uid, config.puid, config.pgid);

    match execute(config, &request, &connection) {
        Ok(Some(response)) => {
            let mut json = serde_json::to_string(&response).expect("a response always serializes");
            json.push('\n');
            let _ = answer.write_all(json.as_bytes());
            0
        }
        // Nobody is listening for an answer to a cancelled call.
        Ok(None) => 0,
        Err(e) => fail(&mut answer, &format!("cannot run the command: {e}")),
    }
}

fn fail(answer: &mut UnixStream, error: &str) -> i32 {
    let mut json = serde_json::to_string(&ExecFailure { error: error.to_string() }).unwrap();
    json.push('\n');
    let _ = answer.write_all(json.as_bytes());
    1
}

// A private mount namespace, so what this exec mounts nobody else sees, and the unit a subreaper,
// so every process the command leaves behind is still the unit's to kill.
fn isolate() -> io::Result<()> {
    unsafe {
        if libc::unshare(libc::CLONE_NEWNS) == -1 {
            return Err(io::Error::last_os_error());
        }
        let root = CString::new("/").unwrap();
        if libc::mount(std::ptr::null(), root.as_ptr(), std::ptr::null(), libc::MS_REC | libc::MS_PRIVATE, std::ptr::null()) == -1 {
            return Err(io::Error::last_os_error());
        }
        if libc::prctl(libc::PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0) == -1 {
            return Err(io::Error::last_os_error());
        }
    }
    Ok(())
}

fn execute(config: &UnitConfig, request: &ExecRequest, connection: &UnixStream) -> io::Result<Option<ExecResponse>> {
    // The other mounts, served for this call only. A daemon that cannot mount costs the command its
    // view of them, never the command itself: it runs, and its stderr says why /vfs is missing.
    let (served, unserved) = match &request.bridge {
        Some(grant) => match Served::start(grant, config.puid, config.pgid) {
            Ok(served) => (Some(served), None),
            Err(e) => (None, Some(format!("sandbox-launcher: the other mounts are not served to this command: {e}\n"))),
        },
        None => (None, None),
    };
    let mut served = served;
    let spare = served.as_ref().map(Served::pid);

    let identity = Identity {
        uid: config.puid,
        gid: config.pgid,
        groups: config.groups.clone(),
        cwd: Some(CString::new(request.cwd.clone())?),
        umask: 0o002,
    };

    let started = Instant::now();
    let mut command = Command::new("bash");
    command
        .arg("-lc")
        .arg(&request.command)
        .env_clear()
        .envs(&request.env)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    unsafe {
        command.pre_exec(move || {
            // Its own process group first, while still root, so the kill can name the group.
            if libc::setsid() == -1 {
                return Err(io::Error::last_os_error());
            }
            become_identity(&identity)
        });
    }
    let mut child = command.spawn()?;

    let deadline = started + Duration::from_secs(request.timeout_seconds);
    let mut stdout = CappedOutput::new(request.output_cap_bytes);
    let mut stderr = CappedOutput::new(request.output_cap_bytes);
    if let Some(note) = &unserved {
        stderr.push(note.as_bytes());
    }
    let mut out = child.stdout.take();
    let mut err = child.stderr.take();
    let (ending, status) = pump(&mut child, &mut out, &mut err, &mut stdout, &mut stderr, connection, deadline)?;

    match ending {
        Ending::TimedOut => {
            // Revoked first, so what the kill flushes reaches the mount as dropped, never applied.
            if let Some(served) = served.as_mut() {
                served.revoke();
            }
            kill_tree(&mut child, spare);
            drain(&mut out, &mut err, &mut stdout, &mut stderr, Instant::now() + DRAIN_AFTER_KILL);
        }
        Ending::Cancelled => kill_tree(&mut child, spare),
        Ending::Finished => {}
    }

    // The daemon's last commits land before the answer, so the exec result's change list is whole.
    if let Some(served) = served {
        served.finish();
    }
    reap_orphans();
    if matches!(ending, Ending::Cancelled) {
        return Ok(None);
    }

    let timed_out = matches!(ending, Ending::TimedOut);
    Ok(Some(ExecResponse {
        truncated: stdout.truncated() || stderr.truncated(),
        stdout: stdout.into_text(),
        stderr: stderr.into_text(),
        exit_code: if timed_out { -1 } else { status.unwrap_or(-1) },
        timed_out,
        duration_ms: started.elapsed().as_millis() as u64,
    }))
}

// Reads both streams until the command and everything holding its pipes are gone, the deadline
// passes, or the server hangs up. The pipes, not the shell's exit, end a normal run: output a
// background job is still writing belongs to this call, and the deadline still bounds it.
fn pump(
    child: &mut Child,
    out: &mut Option<ChildStdout>,
    err: &mut Option<ChildStderr>,
    stdout: &mut CappedOutput,
    stderr: &mut CappedOutput,
    connection: &UnixStream,
    deadline: Instant,
) -> io::Result<(Ending, Option<i32>)> {
    let mut status: Option<i32> = None;
    let mut buffer = [0u8; 8192];

    loop {
        if status.is_none() {
            status = child.try_wait()?.map(exit_code);
        }
        if out.is_none() && err.is_none() && status.is_some() {
            return Ok((Ending::Finished, status));
        }

        let now = Instant::now();
        if now >= deadline {
            return Ok((Ending::TimedOut, status));
        }

        // The child's exit has no fd here, so a short poll keeps the wait for it prompt.
        let wait_ms = (deadline - now).as_millis().min(50) as libc::c_int;
        let mut fds = vec![libc::pollfd { fd: connection.as_raw_fd(), events: libc::POLLRDHUP, revents: 0 }];
        if let Some(o) = out.as_ref() {
            fds.push(libc::pollfd { fd: o.as_raw_fd(), events: libc::POLLIN, revents: 0 });
        }
        if let Some(e) = err.as_ref() {
            fds.push(libc::pollfd { fd: e.as_raw_fd(), events: libc::POLLIN, revents: 0 });
        }
        let ready = unsafe { libc::poll(fds.as_mut_ptr(), fds.len() as libc::nfds_t, wait_ms) };
        if ready == -1 {
            let e = io::Error::last_os_error();
            if e.kind() == io::ErrorKind::Interrupted {
                continue;
            }
            return Err(e);
        }

        if fds[0].revents & (libc::POLLRDHUP | libc::POLLHUP | libc::POLLERR) != 0 {
            return Ok((Ending::Cancelled, status));
        }

        let readable = |fd: RawFd| fds.iter().any(|p| p.fd == fd && p.revents != 0);
        if out.as_ref().is_some_and(|o| readable(o.as_raw_fd())) {
            read_into(out, stdout, &mut buffer);
        }
        if err.as_ref().is_some_and(|e| readable(e.as_raw_fd())) {
            read_into(err, stderr, &mut buffer);
        }
    }
}

fn read_into<R: Read>(stream: &mut Option<R>, sink: &mut CappedOutput, buffer: &mut [u8]) {
    match stream.as_mut().map(|s| s.read(buffer)) {
        Some(Ok(0)) | Some(Err(_)) => *stream = None,
        Some(Ok(n)) => sink.push(&buffer[..n]),
        None => {}
    }
}

fn drain<A: Read + AsRawFd, B: Read + AsRawFd>(
    out: &mut Option<A>,
    err: &mut Option<B>,
    stdout: &mut CappedOutput,
    stderr: &mut CappedOutput,
    until: Instant,
) {
    let mut buffer = [0u8; 8192];
    while (out.is_some() || err.is_some()) && Instant::now() < until {
        let mut fds: Vec<libc::pollfd> = [out.as_ref().map(|o| o.as_raw_fd()), err.as_ref().map(|e| e.as_raw_fd())]
            .into_iter()
            .flatten()
            .map(|fd| libc::pollfd { fd, events: libc::POLLIN, revents: 0 })
            .collect();
        if unsafe { libc::poll(fds.as_mut_ptr(), fds.len() as libc::nfds_t, 50) } <= 0 {
            continue;
        }
        let readable = |fd: RawFd| fds.iter().any(|p| p.fd == fd && p.revents != 0);
        if out.as_ref().is_some_and(|o| readable(o.as_raw_fd())) {
            read_into(out, stdout, &mut buffer);
        }
        if err.as_ref().is_some_and(|e| readable(e.as_raw_fd())) {
            read_into(err, stderr, &mut buffer);
        }
    }
}

fn exit_code(status: std::process::ExitStatus) -> i32 {
    use std::os::unix::process::ExitStatusExt;
    status.code().unwrap_or_else(|| 128 + status.signal().unwrap_or(0))
}

// The command's group first, then every descendant the subreaper collected, until none is left:
// a process can fork between the listing and the kill, so one pass is not enough. The call's
// daemon is the unit's child too, and is spared: its last commits still have to land.
fn kill_tree(child: &mut Child, spare: Option<i32>) {
    unsafe {
        libc::kill(-(child.id() as i32), libc::SIGKILL);
    }
    let me = std::process::id() as i32;
    for _ in 0..10 {
        let living: Vec<i32> = proctree::descendants(me, &proctree::read_parents())
            .into_iter()
            .filter(|&pid| Some(pid) != spare)
            .collect();
        if living.is_empty() {
            break;
        }
        living.iter().for_each(|&pid| unsafe {
            libc::kill(pid, libc::SIGKILL);
        });
        reap_orphans();
    }
    let _ = child.wait();
}

// Orphans the subreaper inherited are its to wait for, or they linger as zombies until it exits.
fn reap_orphans() {
    let mut status = 0;
    while unsafe { libc::waitpid(-1, &mut status, libc::WNOHANG) } > 0 {}
}

/// The unit's configuration, as the launcher passes it in the environment.
pub fn config_from_env(vars: &HashMap<String, String>) -> Option<UnitConfig> {
    let number = |name: &str| vars.get(name)?.parse::<u32>().ok();
    Some(UnitConfig {
        puid: number("PUID")?,
        pgid: number("PGID")?,
        server_uid: number("SANDBOX_SERVER_UID")?,
        home: vars.get("SANDBOX_HOME")?.clone(),
        groups: vars
            .get("SANDBOX_COMMAND_GROUPS")?
            .split(',')
            .filter_map(|g| g.parse().ok())
            .collect(),
    })
}
