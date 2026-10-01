//! What the daemon owns: inode numbers for paths, the per-call cache of what the bridge answered,
//! and open handles. Everything is per exec — the daemon lives for one command — so nothing here
//! is ever forgotten or evicted; a write through the bridge drops the cache whole.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use super::bridge::{Attr, Bridge, Errno, Kind, Listing};

pub const ROOT: u64 = 1;

/// A node as the kernel is told about it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Node {
    pub ino: u64,
    pub kind: Kind,
    /// What `stat` reports. Zero for a file whose size is unknown, which is then opened with direct
    /// I/O so the kernel reads until the daemon says EOF instead of trusting the zero.
    pub size: u64,
    pub size_known: bool,
}

pub struct DirEntry {
    pub ino: u64,
    pub kind: Kind,
    pub name: String,
}

pub struct Opened {
    pub fh: u64,
    pub direct_io: bool,
}

#[derive(Default)]
struct State {
    paths: HashMap<u64, String>,
    inos: HashMap<String, u64>,
    attrs: HashMap<String, Attr>,
    listings: HashMap<String, Listing>,
    contents: HashMap<String, Arc<Vec<u8>>>,
    handles: HashMap<u64, Arc<Vec<u8>>>,
    next_ino: u64,
    next_fh: u64,
}

pub struct Vfs<B: Bridge> {
    bridge: B,
    state: Mutex<State>,
}

impl<B: Bridge> Vfs<B> {
    pub fn new(bridge: B) -> Self {
        let mut state = State { next_ino: ROOT + 1, next_fh: 1, ..State::default() };
        state.paths.insert(ROOT, "/".into());
        state.inos.insert("/".into(), ROOT);
        Self { bridge, state: Mutex::new(state) }
    }

    pub fn bridge(&self) -> &B {
        &self.bridge
    }

    pub fn lookup(&self, parent: u64, name: &str) -> Result<Node, Errno> {
        let parent_path = self.path_of(parent)?;
        let path = join(&parent_path, name);
        // A name the cached listing does not have is answered without a round trip: a shell's
        // PATH search and a `find` probe ask for many names that are not there.
        if let Some(listing) = self.lock().listings.get(&parent_path) {
            if !listing.entries.iter().any(|e| e.name == name) {
                return Err(libc::ENOENT);
            }
        }
        self.node_at(&path)
    }

    pub fn getattr(&self, ino: u64) -> Result<Node, Errno> {
        let path = self.path_of(ino)?;
        self.node_at(&path)
    }

    pub fn readdir(&self, ino: u64) -> Result<Vec<DirEntry>, Errno> {
        let path = self.path_of(ino)?;
        let listing = self.listing(&path)?;
        Ok(listing
            .entries
            .iter()
            .map(|e| DirEntry { ino: self.ino_for(&join(&path, &e.name)), kind: e.kind, name: e.name.clone() })
            .collect())
    }

    /// Reads are whole-file: the bridge answers the file, and the handle serves slices of it. A
    /// write open is refused until writes are served (ticket 08).
    pub fn open(&self, ino: u64, write: bool) -> Result<Opened, Errno> {
        if write {
            return Err(libc::EROFS);
        }
        let path = self.path_of(ino)?;
        let node = self.node_at(&path)?;
        if node.kind != Kind::File {
            return Err(libc::EISDIR);
        }
        let content = self.content(&path)?;
        let mut state = self.lock();
        let fh = state.next_fh;
        state.next_fh += 1;
        state.handles.insert(fh, content);
        Ok(Opened { fh, direct_io: !node.size_known })
    }

    pub fn read(&self, fh: u64, offset: u64, size: u32) -> Result<Vec<u8>, Errno> {
        let content = self.lock().handles.get(&fh).cloned().ok_or(libc::EBADF)?;
        let start = (offset as usize).min(content.len());
        let end = start.saturating_add(size as usize).min(content.len());
        Ok(content[start..end].to_vec())
    }

    pub fn release(&self, fh: u64) {
        self.lock().handles.remove(&fh);
    }

    /// The names served at the root, which is where the launcher puts a link per mount.
    pub fn served(&self) -> Result<Vec<String>, Errno> {
        Ok(self.listing("/")?.entries.into_iter().map(|e| e.name).collect())
    }

    fn node_at(&self, path: &str) -> Result<Node, Errno> {
        let attr = self.attr(path)?;
        let ino = self.ino_for(path);
        let (size, known) = match (attr.size, self.lock().contents.get(path)) {
            (Some(size), _) => (size, true),
            // Once read, a rendered file's length is known for the rest of the call.
            (None, Some(content)) => (content.len() as u64, true),
            (None, None) => (0, false),
        };
        Ok(Node { ino, kind: attr.kind, size, size_known: known || attr.kind != Kind::File })
    }

    fn attr(&self, path: &str) -> Result<Attr, Errno> {
        if let Some(attr) = self.lock().attrs.get(path) {
            return Ok(attr.clone());
        }
        let attr = self.bridge.attr(path)?;
        self.lock().attrs.insert(path.to_string(), attr.clone());
        Ok(attr)
    }

    fn listing(&self, path: &str) -> Result<Listing, Errno> {
        if let Some(listing) = self.lock().listings.get(path) {
            return Ok(listing.clone());
        }
        let listing = self.bridge.list(path)?;
        let mut state = self.lock();
        // A listing says what each entry is, so a walk that lists before it looks costs no attr
        // fetch for a directory.
        listing.entries.iter().filter(|e| e.kind == Kind::Dir).for_each(|e| {
            state.attrs.entry(join(path, &e.name)).or_insert(Attr { kind: Kind::Dir, size: None });
        });
        state.listings.insert(path.to_string(), listing.clone());
        Ok(listing)
    }

    fn content(&self, path: &str) -> Result<Arc<Vec<u8>>, Errno> {
        if let Some(content) = self.lock().contents.get(path) {
            return Ok(content.clone());
        }
        let content = Arc::new(self.bridge.read(path)?);
        self.lock().contents.insert(path.to_string(), content.clone());
        Ok(content)
    }

    fn path_of(&self, ino: u64) -> Result<String, Errno> {
        self.lock().paths.get(&ino).cloned().ok_or(libc::ENOENT)
    }

    fn ino_for(&self, path: &str) -> u64 {
        let mut state = self.lock();
        if let Some(&ino) = state.inos.get(path) {
            return ino;
        }
        let ino = state.next_ino;
        state.next_ino += 1;
        state.inos.insert(path.to_string(), ino);
        state.paths.insert(ino, path.to_string());
        ino
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

pub fn join(parent: &str, name: &str) -> String {
    if parent == "/" {
        format!("/{name}")
    } else {
        format!("{parent}/{name}")
    }
}
