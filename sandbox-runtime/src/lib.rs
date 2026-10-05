//! The sandbox container's privileged half: the root launcher that starts the MCP server as its
//! own uid and runs each command as PUID in a private mount namespace. See `CLAUDE.md`.

#[cfg(target_os = "linux")]
pub mod home;
pub mod vfs;
pub mod output;
pub mod proctree;
pub mod protocol;

#[cfg(target_os = "linux")]
pub mod launcher;
#[cfg(target_os = "linux")]
pub mod privilege;
#[cfg(target_os = "linux")]
pub mod seccomp;
#[cfg(target_os = "linux")]
pub mod served;
#[cfg(target_os = "linux")]
pub mod unit;
