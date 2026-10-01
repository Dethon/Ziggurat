//! `vfs-action`: served, byte for byte, as every action file under /vfs. It runs as the command's
//! own user and holds no token: it asks the call's daemon to run the action it is — learned from
//! its own executable path — and passes the action's output and exit code through as its own.

#[cfg(target_os = "linux")]
fn main() {
    use std::io::{BufRead, BufReader, Write};
    use std::os::unix::net::UnixStream;

    use sandbox_runtime::vfs::actions::{virtual_path, ActionReply, ActionRequest, SOCKET};

    let exe = std::fs::read_link("/proc/self/exe").map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let Some(path) = virtual_path(&exe) else {
        eprintln!("vfs-action: run an action file under /vfs, not the helper itself");
        std::process::exit(126);
    };
    let request = ActionRequest {
        path: path.clone(),
        argv: std::env::args().skip(1).collect(),
        cwd: std::env::current_dir().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
    };

    let Ok(mut connection) = UnixStream::connect(SOCKET) else {
        eprintln!("vfs-action: {path} can only run inside a sandbox command that has the mounts");
        std::process::exit(126);
    };
    let sent = writeln!(connection, "{}", serde_json::to_string(&request).expect("serializes"));
    let mut line = String::new();
    let read = sent.ok().and_then(|_| BufReader::new(&connection).read_line(&mut line).ok());
    match (read, serde_json::from_str::<ActionReply>(&line)) {
        (Some(n), Ok(ActionReply::Ran(output))) if n > 0 => {
            print!("{}", output.stdout);
            eprint!("{}", output.stderr);
            let _ = std::io::stdout().flush();
            std::process::exit(output.exit_code);
        }
        (_, Ok(ActionReply::Refused { errno })) => {
            eprintln!("vfs-action: {path}: {}", std::io::Error::from_raw_os_error(errno));
            std::process::exit(126);
        }
        _ => {
            eprintln!("vfs-action: {path}: the call's daemon did not answer");
            std::process::exit(126);
        }
    }
}

#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("vfs-action runs on Linux only");
    std::process::exit(2);
}
