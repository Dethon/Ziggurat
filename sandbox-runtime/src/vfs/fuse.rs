//! The kernel side: fuser's callbacks, each one a call into the core and a reply. Nothing is
//! decided here beyond spelling — modes, ownership and TTLs.

use std::ffi::OsStr;
use std::time::{Duration, UNIX_EPOCH};

use fuser::{
    Errno, FileAttr, FileHandle, FileType, Filesystem, FopenFlags, Generation, INodeNo, LockOwner, OpenAccMode,
    OpenFlags, ReplyAttr, ReplyData, ReplyDirectory, ReplyEmpty, ReplyEntry, ReplyOpen, Request,
};

use super::bridge::{Bridge, Kind};
use super::core::{Node, Vfs};

/// The kernel asks again every time; the daemon's own per-call cache answers without a round trip.
/// A kernel-side cache would outlive a write the bridge made and serve the old answer.
const TTL: Duration = Duration::ZERO;

pub struct Fuse<B: Bridge> {
    pub vfs: Vfs<B>,
    /// The command's own uid and gid. Every node is theirs, so the kernel's permission check lets
    /// the command write; whether a write is allowed is the bridge's decision, never the mode's.
    pub uid: u32,
    pub gid: u32,
}

impl<B: Bridge> Fuse<B> {
    fn attr(&self, node: &Node) -> FileAttr {
        let (kind, perm, nlink) = match node.kind {
            Kind::Dir => (FileType::Directory, 0o755, 2),
            Kind::File => (FileType::RegularFile, 0o644, 1),
            // Executable-only: the kernel runs it without anyone being able to read it.
            Kind::Action => (FileType::RegularFile, 0o111, 1),
        };
        FileAttr {
            ino: INodeNo(node.ino),
            size: node.size,
            blocks: node.size.div_ceil(512),
            atime: UNIX_EPOCH,
            mtime: UNIX_EPOCH,
            ctime: UNIX_EPOCH,
            crtime: UNIX_EPOCH,
            kind,
            perm,
            nlink,
            uid: self.uid,
            gid: self.gid,
            rdev: 0,
            flags: 0,
            blksize: 4096,
        }
    }
}

fn errno(code: i32) -> Errno {
    Errno::from_i32(code)
}

impl<B: Bridge> Filesystem for Fuse<B> {
    fn lookup(&self, _req: &Request, parent: INodeNo, name: &OsStr, reply: ReplyEntry) {
        match self.vfs.lookup(parent.0, &name.to_string_lossy()) {
            Ok(node) => reply.entry(&TTL, &self.attr(&node), Generation(0)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn getattr(&self, _req: &Request, ino: INodeNo, _fh: Option<FileHandle>, reply: ReplyAttr) {
        match self.vfs.getattr(ino.0) {
            Ok(node) => reply.attr(&TTL, &self.attr(&node)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn open(&self, _req: &Request, ino: INodeNo, flags: OpenFlags, reply: ReplyOpen) {
        let write = flags.acc_mode() != OpenAccMode::O_RDONLY;
        match self.vfs.open(ino.0, write) {
            Ok(opened) => reply.opened(
                FileHandle(opened.fh),
                if opened.direct_io { FopenFlags::FOPEN_DIRECT_IO } else { FopenFlags::empty() },
            ),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn read(
        &self,
        _req: &Request,
        _ino: INodeNo,
        fh: FileHandle,
        offset: u64,
        size: u32,
        _flags: OpenFlags,
        _lock_owner: Option<LockOwner>,
        reply: ReplyData,
    ) {
        match self.vfs.read(fh.0, offset, size) {
            Ok(bytes) => reply.data(&bytes),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn release(
        &self,
        _req: &Request,
        _ino: INodeNo,
        fh: FileHandle,
        _flags: OpenFlags,
        _lock_owner: Option<LockOwner>,
        _flush: bool,
        reply: ReplyEmpty,
    ) {
        self.vfs.release(fh.0);
        reply.ok();
    }

    fn readdir(&self, _req: &Request, ino: INodeNo, _fh: FileHandle, offset: u64, mut reply: ReplyDirectory) {
        let entries = match self.vfs.readdir(ino.0) {
            Ok(entries) => entries,
            Err(e) => return reply.error(errno(e)),
        };
        let dots = [(ino.0, FileType::Directory, ".".to_string()), (ino.0, FileType::Directory, "..".to_string())];
        let all = dots.into_iter().chain(entries.into_iter().map(|e| {
            let kind = if e.kind == Kind::Dir { FileType::Directory } else { FileType::RegularFile };
            (e.ino, kind, e.name)
        }));
        for (index, (child, kind, name)) in all.enumerate().skip(offset as usize) {
            if reply.add(INodeNo(child), (index + 1) as u64, kind, name) {
                break;
            }
        }
        reply.ok();
    }
}
