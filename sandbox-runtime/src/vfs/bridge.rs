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

/// A refusal, as the errno the kernel will be handed.
pub type Errno = i32;

pub trait Bridge: Send + Sync + 'static {
    fn attr(&self, path: &str) -> Result<Attr, Errno>;
    fn list(&self, path: &str) -> Result<Listing, Errno>;
    fn read(&self, path: &str) -> Result<Vec<u8>, Errno>;
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
