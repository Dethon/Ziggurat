//! What the daemon owns: inode numbers for paths, the per-call cache of what the bridge answered,
//! open handles, and the **overlay** — what the command made that the mount has not been told
//! about yet. Everything is per exec (the daemon lives for one command), so nothing is evicted;
//! any mutation through the bridge drops the cache whole, never the overlay.
//!
//! When a write reaches the mount (spike finding 5, and the spec's write semantics):
//! - an existing file commits on the **last release** of its write handles, and only if something
//!   was written or truncated — never on flush, which fires once per duplicated descriptor, so
//!   `echo a > f` would otherwise commit an empty intermediate first;
//! - a **new** file is held until it is renamed onto a path (a write there — temp-then-rename
//!   writers like `sed -i` never commit their temp), an action runs, or the command ends;
//! - a new directory exists only here until a file is committed under it, and arrives with it;
//! - a **delete** is held the same way, and an `rmdir` subsumes every held delete beneath it: GNU
//!   `rm -r` unlinks every child before it removes the directory, and stops at the first child a
//!   mount refuses — a timer's files refuse alone, its directory is the cancel — so the mount sees
//!   the one delete the remove tool makes. At the end, deletes commit before held files.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use super::bridge::{ActionOutput, Attr, Bridge, Errno, Kind, Listing, MAX_FILE};

pub const ROOT: u64 = 1;

/// The kernel releases a dead command's files after the process is gone; the end of the command
/// waits this long for them before committing what is held.
const RELEASE_GRACE: Duration = Duration::from_secs(5);

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

/// A new file: its bytes, and the order it was made in, which is the order it commits in.
struct Held {
    data: Vec<u8>,
    seq: u64,
}

/// An existing file open for writing: the whole content, committed on the last release.
struct Writer {
    data: Vec<u8>,
    open: usize,
    dirty: bool,
}

enum Handle {
    Read(Arc<Vec<u8>>),
    /// Writes into an existing file's writer, keyed by inode.
    Write(u64),
    /// Writes into a held file, keyed by inode: a rename moves the path, never the inode.
    Held(u64),
}

#[derive(Default)]
struct State {
    paths: HashMap<u64, String>,
    inos: HashMap<String, u64>,
    attrs: HashMap<String, Attr>,
    listings: HashMap<String, Listing>,
    contents: HashMap<String, Arc<Vec<u8>>>,
    handles: HashMap<u64, Handle>,
    held_files: BTreeMap<String, Held>,
    held_dirs: BTreeSet<String>,
    /// Paths deleted in the call and not yet on the mount.
    deleted: BTreeSet<String>,
    writers: HashMap<u64, Writer>,
    /// Last releases whose commit has left the writer behind and not yet heard from the bridge.
    committing: usize,
    next_ino: u64,
    next_fh: u64,
    next_seq: u64,
}

impl State {
    fn path(&self, ino: u64) -> Result<String, Errno> {
        self.paths.get(&ino).cloned().ok_or(libc::ENOENT)
    }

    fn ino_for(&mut self, path: &str) -> u64 {
        if let Some(&ino) = self.inos.get(path) {
            return ino;
        }
        let ino = self.next_ino;
        self.next_ino += 1;
        self.inos.insert(path.to_string(), ino);
        self.paths.insert(ino, path.to_string());
        ino
    }

    /// The kernel keeps the inode across a rename, so the daemon moves the path, not the number.
    fn move_ino(&mut self, from: &str, to: &str) {
        if let Some(ino) = self.inos.remove(from) {
            if let Some(old) = self.inos.insert(to.to_string(), ino) {
                self.paths.remove(&old);
            }
            self.paths.insert(ino, to.to_string());
        }
    }

    /// A rename of `from` and of everything known under it — the inodes the kernel holds, and what
    /// the overlay holds there.
    fn move_tree(&mut self, from: &str, to: &str) {
        let known: Vec<String> = self.inos.keys().filter(|p| is_within(p, from)).cloned().collect();
        known.iter().for_each(|p| self.move_ino(p, &rebase(p, from, to)));
        self.held_dirs = std::mem::take(&mut self.held_dirs)
            .into_iter()
            .map(|p| if is_within(&p, from) { rebase(&p, from, to) } else { p })
            .collect();
        self.held_files = std::mem::take(&mut self.held_files)
            .into_iter()
            .map(|(p, file)| if is_within(&p, from) { (rebase(&p, from, to), file) } else { (p, file) })
            .collect();
    }

    /// A held file has reached the mount at `path` while descriptors were still open on it. They
    /// go on as an existing file's do: what they write from here commits on their last release.
    fn carry_on(&mut self, path: &str, data: Vec<u8>) {
        let Some(&ino) = self.inos.get(path) else {
            return;
        };
        let mut open = 0;
        self.handles.values_mut().filter(|handle| matches!(handle, Handle::Held(held) if *held == ino)).for_each(|handle| {
            *handle = Handle::Write(ino);
            open += 1;
        });
        if open > 0 {
            self.writers.insert(ino, Writer { data, open, dirty: false });
        }
    }

    fn held(&self, path: &str) -> Option<Kind> {
        if self.held_dirs.contains(path) {
            Some(Kind::Dir)
        } else if self.held_files.contains_key(path) {
            Some(Kind::File)
        } else {
            None
        }
    }

    /// The overlay's direct children of `dir`.
    fn held_children(&self, dir: &str) -> Vec<(String, Kind)> {
        let dirs = self.held_dirs.iter().map(|p| (p, Kind::Dir));
        let files = self.held_files.keys().map(|p| (p, Kind::File));
        dirs.chain(files)
            .filter(|(p, _)| parent(p) == dir)
            .map(|(p, kind)| (name(p).to_string(), kind))
            .collect()
    }

    /// Deleted in the call, itself or under a deleted directory.
    fn is_deleted(&self, path: &str) -> bool {
        self.deleted.iter().any(|d| is_within(path, d))
    }

    fn forget_answers(&mut self) {
        self.attrs.clear();
        self.listings.clear();
        self.contents.clear();
    }
}

pub struct Vfs<B: Bridge> {
    bridge: B,
    state: Mutex<State>,
    released: Condvar,
    /// The command was revoked and the bridge never heard: nothing more is sent, because an agent
    /// that does not know would apply it.
    sealed: AtomicBool,
    /// The action helper's own bytes, served as every action file: the kernel executes it without
    /// anyone being able to read it, and it learns which action it is from its own path.
    helper: Arc<Vec<u8>>,
    release_grace: Duration,
}

impl<B: Bridge> Vfs<B> {
    pub fn new(bridge: B) -> Self {
        let mut state = State { next_ino: ROOT + 1, next_fh: 1, ..State::default() };
        state.paths.insert(ROOT, "/".into());
        state.inos.insert("/".into(), ROOT);
        Self {
            bridge,
            state: Mutex::new(state),
            released: Condvar::new(),
            sealed: AtomicBool::new(false),
            helper: Arc::new(Vec::new()),
            release_grace: RELEASE_GRACE,
        }
    }

    /// How long the end of the command waits for the kernel's late releases.
    pub fn with_release_grace(mut self, grace: Duration) -> Self {
        self.release_grace = grace;
        self
    }

    pub fn with_helper(mut self, helper: Vec<u8>) -> Self {
        self.helper = Arc::new(helper);
        self
    }

    pub fn bridge(&self) -> &B {
        &self.bridge
    }

    pub fn lookup(&self, parent_ino: u64, name: &str) -> Result<Node, Errno> {
        let parent_path = self.lock().path(parent_ino)?;
        let path = join(&parent_path, name);
        {
            let state = self.lock();
            if state.held(&path).is_none() {
                if state.is_deleted(&path) {
                    return Err(libc::ENOENT);
                }
                // A held directory has nothing on the mount; and a name the cached listing lacks is
                // answered without a round trip — a shell's PATH search and a `find` probe ask for
                // many names that are not there.
                if state.held_dirs.contains(&parent_path) {
                    return Err(libc::ENOENT);
                }
                if let Some(listing) = state.listings.get(&parent_path) {
                    if !listing.entries.iter().any(|e| e.name == name) {
                        return Err(libc::ENOENT);
                    }
                }
            }
        }
        self.node_at(&path)
    }

    pub fn getattr(&self, ino: u64) -> Result<Node, Errno> {
        let path = self.lock().path(ino)?;
        self.node_at(&path)
    }

    pub fn readdir(&self, ino: u64) -> Result<Vec<DirEntry>, Errno> {
        let path = self.lock().path(ino)?;
        let from_mount: Vec<(String, Kind)> = if self.lock().held_dirs.contains(&path) {
            Vec::new()
        } else {
            self.listing(&path)?.entries.into_iter().map(|e| (e.name, e.kind)).collect()
        };
        let mut state = self.lock();
        let mut entries: BTreeMap<String, Kind> = from_mount
            .into_iter()
            .filter(|(name, _)| !state.is_deleted(&join(&path, name)))
            .collect();
        entries.extend(state.held_children(&path));
        Ok(entries
            .into_iter()
            .map(|(name, kind)| DirEntry { ino: state.ino_for(&join(&path, &name)), kind, name })
            .collect())
    }

    /// A new file, held: nothing reaches the mount until it is renamed onto a path, an action
    /// runs, or the command ends.
    pub fn create(&self, parent_ino: u64, name: &str) -> Result<(Node, u64), Errno> {
        let parent_path = self.lock().path(parent_ino)?;
        if self.node_at(&parent_path)?.kind != Kind::Dir {
            return Err(libc::ENOTDIR);
        }
        let path = join(&parent_path, name);
        let mut state = self.lock();
        let seq = state.next_seq;
        state.next_seq += 1;
        state.held_files.insert(path.clone(), Held { data: Vec::new(), seq });
        let ino = state.ino_for(&path);
        let fh = state.next_fh;
        state.next_fh += 1;
        state.handles.insert(fh, Handle::Held(ino));
        Ok((Node { ino, kind: Kind::File, size: 0, size_known: true }, fh))
    }

    pub fn open(&self, ino: u64, write: bool, truncate: bool) -> Result<Opened, Errno> {
        let path = self.lock().path(ino)?;
        let node = self.node_at(&path)?;
        match node.kind {
            Kind::Dir => return Err(libc::EISDIR),
            Kind::Action if write => return Err(libc::EACCES),
            // The kernel opens it to execute it; the command's own read is refused by the 0111
            // mode before it ever gets here.
            Kind::Action => {
                let mut state = self.lock();
                return Ok(Opened { fh: self.add_handle(&mut state, Handle::Read(self.helper.clone())), direct_io: false });
            }
            Kind::File => {}
        }

        // Looked up and used under one lock: an action on another thread commits every held file,
        // and one that left between a check and a take would be a panic, which aborts the daemon.
        {
            let mut state = self.lock();
            if let Some(held) = state.held_files.get_mut(&path) {
                let handle = if write {
                    if truncate {
                        held.data.clear();
                    }
                    Handle::Held(ino)
                } else {
                    Handle::Read(Arc::new(held.data.clone()))
                };
                return Ok(Opened { fh: self.add_handle(&mut state, handle), direct_io: true });
            }
        }

        if write {
            let existing = self.lock().writers.contains_key(&ino);
            let start = if existing || truncate { Vec::new() } else { self.content(&path)?.to_vec() };
            let mut state = self.lock();
            let writer = state.writers.entry(ino).or_insert(Writer { data: start, open: 0, dirty: false });
            writer.open += 1;
            if truncate {
                writer.data.clear();
                writer.dirty = true;
            }
            return Ok(Opened { fh: self.add_handle(&mut state, Handle::Write(ino)), direct_io: true });
        }

        if let Some(writer) = self.lock().writers.get(&ino) {
            let snapshot = Arc::new(writer.data.clone());
            let mut state = self.lock();
            return Ok(Opened { fh: self.add_handle(&mut state, Handle::Read(snapshot)), direct_io: true });
        }
        let content = self.content(&path)?;
        let mut state = self.lock();
        Ok(Opened { fh: self.add_handle(&mut state, Handle::Read(content)), direct_io: !node.size_known })
    }

    pub fn read(&self, fh: u64, offset: u64, size: u32) -> Result<Vec<u8>, Errno> {
        let state = self.lock();
        let slice = |data: &[u8]| {
            let start = (offset as usize).min(data.len());
            let end = start.saturating_add(size as usize).min(data.len());
            data[start..end].to_vec()
        };
        match state.handles.get(&fh).ok_or(libc::EBADF)? {
            Handle::Read(content) => Ok(slice(content)),
            Handle::Write(ino) => Ok(slice(&state.writers.get(ino).ok_or(libc::EBADF)?.data)),
            Handle::Held(ino) => {
                let path = state.path(*ino)?;
                Ok(slice(&state.held_files.get(&path).ok_or(libc::EBADF)?.data))
            }
        }
    }

    pub fn write(&self, fh: u64, offset: u64, data: &[u8]) -> Result<u32, Errno> {
        // Before anything is marked written: a refused write leaves the file as it was.
        let end = fits(offset.saturating_add(data.len() as u64))?;
        let mut state = self.lock();
        let buffer = match state.handles.get(&fh).ok_or(libc::EBADF)? {
            Handle::Read(_) => return Err(libc::EBADF),
            &Handle::Write(ino) => {
                let writer = state.writers.get_mut(&ino).ok_or(libc::EBADF)?;
                writer.dirty = true;
                &mut writer.data
            }
            &Handle::Held(ino) => {
                let path = state.path(ino)?;
                &mut state.held_files.get_mut(&path).ok_or(libc::EBADF)?.data
            }
        };
        if buffer.len() < end {
            buffer.resize(end, 0);
        }
        buffer[offset as usize..end].copy_from_slice(data);
        Ok(data.len() as u32)
    }

    /// Fires once per duplicated descriptor, so it is never a commit.
    pub fn flush(&self, _fh: u64) {}

    pub fn release(&self, fh: u64) {
        let commit = {
            let mut state = self.lock();
            match state.handles.remove(&fh) {
                Some(Handle::Write(ino)) => {
                    let last = state.writers.get_mut(&ino).map(|w| {
                        w.open -= 1;
                        w.open == 0
                    });
                    match last {
                        Some(true) => state.writers.remove(&ino).and_then(|writer| {
                            let commit = writer.dirty.then(|| (state.path(ino), writer.data));
                            state.committing += usize::from(commit.is_some());
                            commit
                        }),
                        _ => None,
                    }
                }
                _ => None,
            }
        };
        if let Some((path, data)) = commit {
            if let Ok(path) = path {
                let _ = self.commit_write(&path, &data, false);
            }
            self.lock().committing -= 1;
        }
        self.released.notify_all();
    }

    /// A size change resizes what is being written, or — on a file nobody has open — is a write of
    /// the shortened content. Mode, owner and times are accepted and change nothing: whether a write
    /// is allowed is the bridge's decision, never the mode's, and `sed -i` fchmods its temp file.
    pub fn setattr(&self, ino: u64, size: Option<u64>) -> Result<Node, Errno> {
        let path = self.lock().path(ino)?;
        if let Some(size) = size {
            let size = fits(size)? as u64;
            let resized_in_place = {
                let mut state = self.lock();
                if let Some(held) = state.held_files.get_mut(&path) {
                    held.data.resize(size as usize, 0);
                    true
                } else if let Some(writer) = state.writers.get_mut(&ino) {
                    writer.data.resize(size as usize, 0);
                    writer.dirty = true;
                    true
                } else {
                    false
                }
            };
            if !resized_in_place {
                let mut data = self.content(&path)?.to_vec();
                data.resize(size as usize, 0);
                self.commit_write(&path, &data, false)?;
            }
        }
        self.node_at(&path)
    }

    /// A new directory lives here until a file is committed under it.
    pub fn mkdir(&self, parent_ino: u64, name: &str) -> Result<Node, Errno> {
        let path = join(&self.lock().path(parent_ino)?, name);
        // A path deleted earlier in the command is free again, as it would be on a disk.
        if !self.lock().is_deleted(&path) && self.node_at(&path).is_ok() {
            return Err(libc::EEXIST);
        }
        let mut state = self.lock();
        state.held_dirs.insert(path.clone());
        Ok(Node { ino: state.ino_for(&path), kind: Kind::Dir, size: 0, size_known: true })
    }

    /// A new file goes as if it never was; one on the mount is deleted when the command commits.
    pub fn unlink(&self, parent_ino: u64, name: &str) -> Result<(), Errno> {
        let path = join(&self.lock().path(parent_ino)?, name);
        if self.lock().held_files.remove(&path).is_some() {
            return Ok(());
        }
        if self.node_at(&path)?.kind == Kind::Dir {
            return Err(libc::EISDIR);
        }
        self.lock().deleted.insert(path);
        Ok(())
    }

    pub fn rmdir(&self, parent_ino: u64, name: &str) -> Result<(), Errno> {
        let path = join(&self.lock().path(parent_ino)?, name);
        let ino = self.lookup(parent_ino, name)?.ino;
        if self.node_at(&path)?.kind != Kind::Dir {
            return Err(libc::ENOTDIR);
        }
        if !self.readdir(ino)?.is_empty() {
            return Err(libc::ENOTEMPTY);
        }
        let mut state = self.lock();
        if state.held_dirs.remove(&path) {
            return Ok(());
        }
        // The directory's own delete is the one the mount sees; what was deleted under it goes
        // with it.
        state.deleted.retain(|p| !is_within(p, &path));
        state.deleted.insert(path);
        Ok(())
    }

    pub fn rename(&self, parent_ino: u64, name: &str, new_parent_ino: u64, new_name: &str) -> Result<(), Errno> {
        let (from, to) = {
            let state = self.lock();
            (join(&state.path(parent_ino)?, name), join(&state.path(new_parent_ino)?, new_name))
        };
        let held = self.lock().held(&from);
        match held {
            // A new file renamed onto a path is a write there: the temp it was never reaches the
            // mount, and the target is judged as the file the command meant to write.
            Some(Kind::File) => {
                // The file is taken under the lock it is looked up under. Where an action on
                // another thread committed it in between, it is on the mount now and the rename is
                // the mount's: asked again, it finds nothing held and takes that road.
                {
                    let mut state = self.lock();
                    if state.held_files.contains_key(&to) {
                        let Some(file) = state.held_files.remove(&from) else {
                            drop(state);
                            return self.rename(parent_ino, name, new_parent_ino, new_name);
                        };
                        state.held_files.insert(to.clone(), file);
                        state.move_ino(&from, &to);
                        return Ok(());
                    }
                }
                // A rename commits now, so what the command deleted before it commits first: a delete
                // left for the end would remove what the rename just put there.
                self.commit_deletes();
                let new = self.node_at(&to).is_err();
                let Some(file) = self.lock().held_files.remove(&from) else {
                    return self.rename(parent_ino, name, new_parent_ino, new_name);
                };
                // The file moves only once the mount took it. A refused rename is one the command
                // was told failed: the file is still where it made it, under the inode it had.
                let result = self.commit_write(&to, &file.data, new);
                let mut state = self.lock();
                match result {
                    Ok(()) => {
                        state.move_ino(&from, &to);
                        state.carry_on(&to, file.data);
                    }
                    Err(_) => {
                        state.held_files.entry(from).or_insert(file);
                    }
                }
                result
            }
            Some(Kind::Dir) => {
                self.lock().move_tree(&from, &to);
                Ok(())
            }
            Some(Kind::Action) => Err(libc::EACCES),
            None => {
                // Something on the mount: the bridge makes it the mount's move, or a transfer when
                // the rename crosses mounts. Something already at the target is the daemon's to say.
                // It commits now, as a held file's rename does, so the deletes before it commit
                // first: one left for the end would take what was just moved into the directory it
                // names, and a target deleted a moment ago is a free path, not one to overwrite.
                self.commit_deletes();
                let overwrite = self.lock().held_files.contains_key(&to) || self.node_at(&to).is_ok();
                let result = self.unsealed().and_then(|()| self.bridge.rename(&from, &to, overwrite));
                let mut state = self.lock();
                state.forget_answers();
                if result.is_ok() {
                    state.held_files.remove(&to);
                    // The directories the command made above the target arrived with the move, and
                    // are the mount's now — as for a committed write.
                    state.held_dirs.retain(|dir| !is_within(&to, dir));
                    // What the command made under a directory goes where the directory went: held
                    // at the old path, it would commit there and bring the directory back.
                    state.move_tree(&from, &to);
                }
                result
            }
        }
    }

    /// An action run from a script. Everything the script made or deleted so far reaches the mount
    /// first — the barrier — so the action sees what the script prepared.
    pub fn action(&self, path: &str, argv: &[String]) -> Result<ActionOutput, Errno> {
        self.commit_deletes();
        self.commit_held();
        let output = self.unsealed().and_then(|()| self.bridge.action(path, argv));
        self.lock().forget_answers();
        output
    }

    /// Ahead of the kill. The daemon keeps sending what the kill flushes — the kernel's releases of
    /// the dead command's files, and everything held — so the bridge can record each as dropped.
    /// Unless the bridge never heard the revocation: then it would apply them, so the daemon seals
    /// itself and sends nothing more.
    pub fn revoke(&self) {
        if self.bridge.revoke().is_err() {
            self.sealed.store(true, Ordering::SeqCst);
        }
    }

    /// The one check every change passes on its way to the bridge.
    fn unsealed(&self) -> Result<(), Errno> {
        if self.sealed.load(Ordering::SeqCst) {
            Err(libc::EACCES)
        } else {
            Ok(())
        }
    }

    /// The command is over: wait for the kernel's late releases and the commits they started —
    /// exec answers once this returns, and the change log must already hold them — then commit
    /// everything held.
    pub fn finish(&self) {
        let deadline = Instant::now() + self.release_grace;
        let mut state = self.lock();
        while (!state.writers.is_empty() || state.committing > 0) && Instant::now() < deadline {
            state = self
                .released
                .wait_timeout(state, deadline.saturating_duration_since(Instant::now()))
                .map(|(guard, _)| guard)
                .unwrap_or_else(|poisoned| poisoned.into_inner().0);
        }
        // A file something still has open — a job that outlived the command — commits as it stands:
        // the mount goes with this daemon, so there is no later release to commit it on.
        let unreleased: Vec<(String, Vec<u8>)> = std::mem::take(&mut state.writers)
            .into_iter()
            .filter(|(_, writer)| writer.dirty)
            .filter_map(|(ino, writer)| state.path(ino).ok().map(|path| (path, writer.data)))
            .collect();
        drop(state);
        unreleased.iter().for_each(|(path, data)| {
            let _ = self.commit_write(path, data, false);
        });
        self.commit_deletes();
        self.commit_held();
    }

    /// Every held delete, shallowest first. With none held nothing is asked and nothing forgotten:
    /// a rename asks this every time, and a walk's cache must survive a `mv` that deleted nothing.
    pub fn commit_deletes(&self) {
        let deleted = std::mem::take(&mut self.lock().deleted);
        if deleted.is_empty() {
            return;
        }
        deleted.iter().for_each(|path| {
            let _ = self.unsealed().and_then(|()| self.bridge.delete(path));
        });
        self.lock().forget_answers();
    }

    /// Every held file, in the order the command made them.
    pub fn commit_held(&self) {
        let held: Vec<(String, Vec<u8>)> = {
            let mut state = self.lock();
            let mut files: Vec<(String, Held)> = std::mem::take(&mut state.held_files).into_iter().collect();
            files.sort_by_key(|(_, h)| h.seq);
            files.into_iter().map(|(p, h)| (p, h.data)).collect()
        };
        // A directory nothing landed in stays held: after an action the command can still fill it.
        held.into_iter().for_each(|(path, data)| {
            if self.commit_write(&path, &data, true).is_ok() {
                self.lock().carry_on(&path, data);
            }
        });
    }

    /// The names served at the root, which is where the launcher puts a link per mount.
    pub fn served(&self) -> Result<Vec<String>, Errno> {
        Ok(self.listing("/")?.entries.into_iter().map(|e| e.name).collect())
    }

    fn commit_write(&self, path: &str, data: &[u8], new: bool) -> Result<(), Errno> {
        let result = self.unsealed().and_then(|()| self.bridge.write(path, data, new));
        let mut state = self.lock();
        state.forget_answers();
        // The directories the command made above it arrived with it, and are the mount's now.
        if result.is_ok() {
            state.held_dirs.retain(|dir| !is_within(path, dir));
        }
        result
    }

    fn add_handle(&self, state: &mut State, handle: Handle) -> u64 {
        let fh = state.next_fh;
        state.next_fh += 1;
        state.handles.insert(fh, handle);
        fh
    }

    fn node_at(&self, path: &str) -> Result<Node, Errno> {
        {
            let mut state = self.lock();
            match state.held(path) {
                Some(Kind::Dir) => return Ok(Node { ino: state.ino_for(path), kind: Kind::Dir, size: 0, size_known: true }),
                Some(_) => {
                    let size = state.held_files[path].data.len() as u64;
                    return Ok(Node { ino: state.ino_for(path), kind: Kind::File, size, size_known: true });
                }
                None => {}
            }
        }
        let attr = self.attr(path)?;
        let mut state = self.lock();
        let ino = state.ino_for(path);
        let (size, known) = match (state.writers.get(&ino), attr.size, state.contents.get(path)) {
            // While a command writes it, a file is as long as what it holds: the kernel computes
            // an O_APPEND offset from it.
            (Some(writer), _, _) => (writer.data.len() as u64, true),
            (None, Some(size), _) => (size, true),
            // Once read, a rendered file's length is known for the rest of the call.
            (None, None, Some(content)) => (content.len() as u64, true),
            (None, None, None) => (0, false),
        };
        if attr.kind == Kind::Action {
            return Ok(Node { ino, kind: Kind::Action, size: self.helper.len() as u64, size_known: true });
        }
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

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

/// A size the daemon will hold, or EFBIG: the command names it, and the daemon allocates it.
fn fits(size: u64) -> Result<usize, Errno> {
    if size > MAX_FILE {
        Err(libc::EFBIG)
    } else {
        Ok(size as usize)
    }
}

pub fn join(parent: &str, name: &str) -> String {
    if parent == "/" {
        format!("/{name}")
    } else {
        format!("{parent}/{name}")
    }
}

fn parent(path: &str) -> &str {
    match path.rfind('/') {
        Some(0) => "/",
        Some(i) => &path[..i],
        None => "/",
    }
}

fn name(path: &str) -> &str {
    &path[path.rfind('/').map_or(0, |i| i + 1)..]
}

fn is_within(path: &str, dir: &str) -> bool {
    path == dir || path.starts_with(&format!("{dir}/"))
}

fn rebase(path: &str, from: &str, to: &str) -> String {
    format!("{to}{}", &path[from.len()..])
}
