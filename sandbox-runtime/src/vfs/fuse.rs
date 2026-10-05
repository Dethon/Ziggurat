//! The kernel side: fuser's callbacks, each one a call into the core and a reply. Nothing is
//! decided here beyond spelling — modes, ownership and TTLs.

use std::ffi::OsStr;
use std::time::{Duration, UNIX_EPOCH};

use std::time::SystemTime;

use fuser::{
    BsdFileFlags, Errno, FileAttr, FileHandle, FileType, Filesystem, FopenFlags, Generation, INodeNo, LockOwner,
    OpenAccMode, OpenFlags, RenameFlags, ReplyAttr, ReplyCreate, ReplyData, ReplyDirectory, ReplyEmpty, ReplyEntry,
    ReplyOpen, ReplyStatfs, ReplyWrite, Request, TimeOrNow, WriteFlags,
};

use super::bridge::{Bridge, Kind};
use super::core::{Node, Vfs};

/// The kernel asks again every time; the daemon's own per-call cache answers without a round trip.
/// A kernel-side cache would outlive a write the bridge made and serve the old answer.
const TTL: Duration = Duration::ZERO;

pub struct Fuse<B: Bridge> {
    /// Shared with the daemon's control loop, which ends the command through it.
    pub vfs: std::sync::Arc<Vfs<B>>,
    /// The command's own uid and gid. Every node is theirs, so the kernel's permission check lets
    /// the command write; whether a write is allowed is the bridge's decision, never the mode's.
    pub uid: u32,
    pub gid: u32,
    /// The unit that started this daemon: the process every process of this exec descends from.
    pub unit: i32,
}

impl<B: Bridge> Fuse<B> {
    /// Whether a request comes from a process of this exec. The mount is `allow_other` and every
    /// command in the container runs as one uid, so the kernel lets any of them in: a command of
    /// another session reaches this mount through `/proc/<pid>/root` of one of this exec's
    /// processes, its own namespace notwithstanding. Asked wherever a name or an inode is turned
    /// into something — never of a read, a write or a release, which carry a handle only an
    /// admitted open could have made. Not cached: a pid is reused, and a command can make one be.
    fn admits(&self, req: &Request) -> bool {
        crate::proctree::descends_from(req.pid() as i32, self.unit, crate::proctree::parent_of)
    }

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
    fn lookup(&self, req: &Request, parent: INodeNo, name: &OsStr, reply: ReplyEntry) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.lookup(parent.0, &name.to_string_lossy()) {
            Ok(node) => reply.entry(&TTL, &self.attr(&node), Generation(0)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn getattr(&self, req: &Request, ino: INodeNo, _fh: Option<FileHandle>, reply: ReplyAttr) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.getattr(ino.0) {
            Ok(node) => reply.attr(&TTL, &self.attr(&node)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn open(&self, req: &Request, ino: INodeNo, flags: OpenFlags, reply: ReplyOpen) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        let write = flags.acc_mode() != OpenAccMode::O_RDONLY;
        let truncate = flags.0 & libc::O_TRUNC != 0;
        match self.vfs.open(ino.0, write, truncate) {
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

    fn create(
        &self,
        req: &Request,
        parent: INodeNo,
        name: &OsStr,
        _mode: u32,
        _umask: u32,
        _flags: i32,
        reply: ReplyCreate,
    ) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.create(parent.0, &name.to_string_lossy()) {
            Ok((node, fh)) => reply.created(
                &TTL,
                &self.attr(&node),
                Generation(0),
                FileHandle(fh),
                FopenFlags::FOPEN_DIRECT_IO,
            ),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn write(
        &self,
        _req: &Request,
        _ino: INodeNo,
        fh: FileHandle,
        offset: u64,
        data: &[u8],
        _write_flags: WriteFlags,
        _flags: OpenFlags,
        _lock_owner: Option<LockOwner>,
        reply: ReplyWrite,
    ) {
        match self.vfs.write(fh.0, offset, data) {
            Ok(written) => reply.written(written),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn flush(&self, _req: &Request, _ino: INodeNo, fh: FileHandle, _lock_owner: LockOwner, reply: ReplyEmpty) {
        self.vfs.flush(fh.0);
        reply.ok();
    }

    fn fsync(&self, _req: &Request, _ino: INodeNo, _fh: FileHandle, _datasync: bool, reply: ReplyEmpty) {
        reply.ok();
    }

    fn setattr(
        &self,
        req: &Request,
        ino: INodeNo,
        _mode: Option<u32>,
        _uid: Option<u32>,
        _gid: Option<u32>,
        size: Option<u64>,
        _atime: Option<TimeOrNow>,
        _mtime: Option<TimeOrNow>,
        _ctime: Option<SystemTime>,
        _fh: Option<FileHandle>,
        _crtime: Option<SystemTime>,
        _chgtime: Option<SystemTime>,
        _bkuptime: Option<SystemTime>,
        _flags: Option<BsdFileFlags>,
        reply: ReplyAttr,
    ) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.setattr(ino.0, size) {
            Ok(node) => reply.attr(&TTL, &self.attr(&node)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn mkdir(&self, req: &Request, parent: INodeNo, name: &OsStr, _mode: u32, _umask: u32, reply: ReplyEntry) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.mkdir(parent.0, &name.to_string_lossy()) {
            Ok(node) => reply.entry(&TTL, &self.attr(&node), Generation(0)),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn unlink(&self, req: &Request, parent: INodeNo, name: &OsStr, reply: ReplyEmpty) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.unlink(parent.0, &name.to_string_lossy()) {
            Ok(()) => reply.ok(),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn rmdir(&self, req: &Request, parent: INodeNo, name: &OsStr, reply: ReplyEmpty) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.rmdir(parent.0, &name.to_string_lossy()) {
            Ok(()) => reply.ok(),
            Err(e) => reply.error(errno(e)),
        }
    }

    fn rename(
        &self,
        req: &Request,
        parent: INodeNo,
        name: &OsStr,
        newparent: INodeNo,
        newname: &OsStr,
        _flags: RenameFlags,
        reply: ReplyEmpty,
    ) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
        match self.vfs.rename(parent.0, &name.to_string_lossy(), newparent.0, &newname.to_string_lossy()) {
            Ok(()) => reply.ok(),
            Err(e) => reply.error(errno(e)),
        }
    }

    // Some writers check for free space before they write; the mounts have no such number, so the
    // answer is generous rather than a zero that reads as full.
    fn statfs(&self, _req: &Request, _ino: INodeNo, reply: ReplyStatfs) {
        reply.statfs(1 << 30, 1 << 30, 1 << 30, 1 << 20, 1 << 20, 4096, 255, 4096);
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

    fn readdir(&self, req: &Request, ino: INodeNo, _fh: FileHandle, offset: u64, mut reply: ReplyDirectory) {
        if !self.admits(req) {
            return reply.error(errno(libc::EACCES));
        }
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
