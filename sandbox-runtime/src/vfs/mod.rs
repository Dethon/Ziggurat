//! The per-exec FUSE daemon: the mounts of the calling session, served at /vfs inside one
//! command's namespace. It is a thin kernel adapter — every decision about what an operation means
//! is the agent's bridge's (`Domain/Tools/FileSystem/Bridge/`), asked one request per operation.
//! `core` holds what the daemon itself must get right (inodes, the per-call cache, handles and,
//! from ticket 08 on, when a write commits) behind the `Bridge` trait, so `cargo test` drives it
//! with a fake bridge and no kernel.

pub mod actions;
pub mod bridge;
pub mod core;

#[cfg(target_os = "linux")]
pub mod fuse;
#[cfg(target_os = "linux")]
pub mod http;
#[cfg(target_os = "linux")]
pub mod daemon;
