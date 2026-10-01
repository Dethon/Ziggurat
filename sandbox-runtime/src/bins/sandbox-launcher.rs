//! `sandbox-launcher serve -- <server command...>` is the sandbox container's entrypoint (as
//! root); `sandbox-launcher unit` is one exec, started by the launcher itself.

use std::collections::HashMap;

#[cfg(target_os = "linux")]
fn main() {
    use sandbox_runtime::{launcher, unit};

    let args: Vec<String> = std::env::args().collect();
    let vars: HashMap<String, String> = std::env::vars().collect();
    let code = match args.get(1).map(String::as_str) {
        Some("unit") => match unit::config_from_env(&vars) {
            Some(config) => unit::run(&config),
            None => {
                eprintln!("sandbox-launcher unit: missing configuration");
                2
            }
        },
        Some("serve") => {
            let server_command = args.iter().skip_while(|a| *a != "--").skip(1).cloned().collect();
            let number = |name: &str, default: u32| vars.get(name).and_then(|v| v.parse().ok()).unwrap_or(default);
            let config = launcher::LauncherConfig {
                puid: number("PUID", 1654),
                pgid: number("PGID", 1654),
                server_uid: number("SANDBOX_SERVER_UID", 1700),
                daemon_uid: number("SANDBOX_DAEMON_UID", 1701),
                home: vars.get("SANDBOX_HOME").cloned().unwrap_or_else(|| "/home/sandbox_user".into()),
                socket: vars.get("LAUNCHER_SOCKET").cloned().unwrap_or_else(|| "/run/sandbox/launcher.sock".into()),
                server_command,
            };
            launcher::serve(config).unwrap_or_else(|e| {
                eprintln!("sandbox-launcher: {e}");
                1
            })
        }
        _ => {
            eprintln!("usage: sandbox-launcher serve -- <server command...> | sandbox-launcher unit");
            2
        }
    };
    std::process::exit(code);
}

#[cfg(not(target_os = "linux"))]
fn main() {
    let _: HashMap<String, String> = HashMap::new();
    eprintln!("sandbox-launcher runs on Linux only");
    std::process::exit(2);
}
