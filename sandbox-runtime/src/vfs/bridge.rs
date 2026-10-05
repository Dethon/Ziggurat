//! The agent's bridge as the daemon sees it. Paths are the daemon's view under /vfs, which is the
//! virtual path the file tools take (`/vault/notes/a.md`); `/` is the root listing the served mounts.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    File,
    Dir,
    /// An action file: executable-only, never opened.
    Action,
}

impl Kind {
    pub fn parse(kind: &str) -> Kind {
        match kind {
            "dir" => Kind::Dir,
            "action" => Kind::Action,
            _ => Kind::File,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Attr {
    pub kind: Kind,
    /// None where the mount cannot tell without rendering the file.
    pub size: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub name: String,
    pub kind: Kind,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Listing {
    pub entries: Vec<Entry>,
    /// The mount's walk stopped before the directory ended.
    pub truncated: bool,
}

/// What an action printed and how it ended.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActionOutput {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

/// A refusal, as the errno the kernel will be handed.
pub type Errno = i32;

pub trait Bridge: Send + Sync + 'static {
    fn attr(&self, path: &str) -> Result<Attr, Errno>;
    fn list(&self, path: &str) -> Result<Listing, Errno>;
    fn read(&self, path: &str) -> Result<Vec<u8>, Errno>;

    /// The whole file, as the equivalent tool call would write it. `new` is the daemon's knowledge
    /// that nothing was at the path, which the bridge would otherwise need a round trip to learn.
    fn write(&self, path: &str, content: &[u8], new: bool) -> Result<(), Errno>;

    /// The remove tool's call: a file, or a directory and everything under it.
    fn delete(&self, path: &str) -> Result<(), Errno>;

    /// One rename, within a mount or across two: the bridge makes it the mount's move or a transfer.
    /// `overwrite` is the daemon's knowledge that something is at `to`, which the bridge judges as
    /// a write there.
    fn rename(&self, from: &str, to: &str, overwrite: bool) -> Result<(), Errno>;

    /// The command is about to be killed: what arrives from now on is dropped, and logged so.
    fn revoke(&self) -> Result<(), Errno>;

    /// An action file run from a script: the mount's exec on the action's directory.
    fn action(&self, path: &str, argv: &[String]) -> Result<ActionOutput, Errno>;
}

/// The bridge's errno names, as the agent spells them (`Errnos` in the bridge).
pub fn errno_named(name: &str) -> Errno {
    match name {
        "ENOENT" => libc::ENOENT,
        "EACCES" => libc::EACCES,
        "EROFS" => libc::EROFS,
        "EEXIST" => libc::EEXIST,
        "EINVAL" => libc::EINVAL,
        "ENOTSUP" => libc::ENOTSUP,
        "ETIMEDOUT" => libc::ETIMEDOUT,
        "ENOTEMPTY" => libc::ENOTEMPTY,
        "EISDIR" => libc::EISDIR,
        "ENOTDIR" => libc::ENOTDIR,
        _ => libc::EIO,
    }
}
