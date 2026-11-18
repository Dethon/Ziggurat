//! The home volume is shared by two uids: commands run as PUID, which owns it, and the sandbox
//! MCP server runs as its own uid so a command cannot read, trace or kill it. The file tools run
//! inside that server, so the server has to reach the volume too. It does so as a member of PGID:
//! the volume is kept group-writable (directories setgid, so new entries stay in PGID), commands
//! and the server both run with umask 002, and what the server's file tools wrote is handed to
//! PUID before each command (the server names it in the request), so a command can `chmod +x` a
//! script the file tool wrote.
//!
//! What this cannot reach: a file a command deliberately creates 0600 (an ssh key, mkstemp) stays
//! unreadable to the file tools. Accepted; such files are a command's own business.

use std::ffi::{CString, OsStr};
use std::fs::{self, File};
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Component, Path, PathBuf};

const GROUP_RW: u32 = 0o060;
const GROUP_X: u32 = 0o010;
const USER_X: u32 = 0o100;
const SETGID: u32 = 0o2000;

/// The mode an entry should have so PGID can do what its owner can. `None` when it already does.
pub fn shared_mode(mode: u32, is_dir: bool) -> Option<u32> {
    let wanted = if is_dir {
        mode | GROUP_RW | GROUP_X | SETGID
    } else if mode & USER_X != 0 {
        mode | GROUP_RW | GROUP_X
    } else {
        mode | GROUP_RW
    };
    (wanted != mode).then_some(wanted)
}

/// One entry of the walk, held by a descriptor on the entry itself — `O_PATH`, never through a
/// link. The tree belongs to PUID, and a command can put anything at a name between the moment
/// root looks at it and the moment root acts on it: a link to `/usr/local/bin` where a directory
/// was, and root has handed the image's binaries to the command. So nothing here acts on a name.
/// What was looked at is what is changed, and what is entered.
pub struct Entry {
    /// Where the entry was when the walk reached it. For the visitor to read, never to act on.
    pub path: PathBuf,
    pub meta: fs::Metadata,
    held: File,
}

impl Entry {
    fn hold(dir: RawFd, name: &OsStr, path: PathBuf) -> io::Result<Entry> {
        let name = CString::new(name.as_bytes())?;
        let fd = unsafe { libc::openat(dir, name.as_ptr(), libc::O_PATH | libc::O_NOFOLLOW | libc::O_CLOEXEC) };
        if fd == -1 {
            return Err(io::Error::last_os_error());
        }
        let held = unsafe { File::from_raw_fd(fd) };
        let meta = held.metadata()?;
        Ok(Entry { path, meta, held })
    }

    /// The held entry as a path, for what only takes one: the kernel resolves it to the inode the
    /// descriptor holds, not to a name in the tree.
    fn by_descriptor(&self) -> String {
        format!("/proc/self/fd/{}", self.held.as_raw_fd())
    }

    /// `None` leaves that half as it is. A link is chowned itself, never what it names.
    pub fn chown(&self, uid: Option<u32>, gid: Option<u32>) -> io::Result<()> {
        let unchanged = u32::MAX;
        let result = unsafe {
            libc::fchownat(
                self.held.as_raw_fd(),
                c"".as_ptr(),
                uid.unwrap_or(unchanged),
                gid.unwrap_or(unchanged),
                libc::AT_EMPTY_PATH,
            )
        };
        if result == -1 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    /// A link has no mode of its own, and going through the descriptor would reach what it names.
    pub fn chmod(&self, mode: u32) -> io::Result<()> {
        if self.meta.file_type().is_symlink() {
            return Ok(());
        }
        fs::set_permissions(self.by_descriptor(), fs::Permissions::from_mode(mode))
    }

    /// The names inside a held directory, listed through the descriptor.
    fn names(&self) -> Vec<std::ffi::OsString> {
        fs::read_dir(self.by_descriptor())
            .map(|entries| entries.filter_map(Result::ok).map(|e| e.file_name()).collect())
            .unwrap_or_default()
    }
}

/// Every entry under `root`, links not followed, `root` included. Unreadable subtrees are skipped:
/// a permissions pass must never be the reason the container does not start.
pub fn walk(root: &Path, visit: &mut dyn FnMut(&Entry)) {
    if let Ok(entry) = Entry::hold(libc::AT_FDCWD, root.as_os_str(), root.to_path_buf()) {
        descend(entry, visit);
    }
}

fn descend(entry: Entry, visit: &mut dyn FnMut(&Entry)) {
    visit(&entry);
    if !entry.meta.is_dir() {
        return;
    }
    entry.names().iter().for_each(|name| {
        if let Ok(child) = Entry::hold(entry.held.as_raw_fd(), name, entry.path.join(name)) {
            descend(child, visit);
        }
    });
}

/// At boot: make the volume group-shared, and hand PUID whatever the server created that no
/// command adopted before the container stopped. PUID's own entries also move into PGID, which is
/// what an entry made before this layout existed may lack.
pub fn share(root: &Path, server_uid: u32, puid: u32, pgid: u32) {
    walk(root, &mut |entry| {
        if entry.meta.uid() == server_uid {
            let _ = entry.chown(Some(puid), Some(pgid));
        }
        if entry.meta.file_type().is_symlink() {
            return;
        }
        if entry.meta.uid() == puid && entry.meta.gid() != pgid {
            let _ = entry.chown(None, Some(pgid));
        }
        if let Some(mode) = shared_mode(entry.meta.mode() & 0o7777, entry.meta.is_dir()) {
            let _ = entry.chmod(mode);
        }
    });
}

/// Before each command: whatever the server's file tools wrote becomes PUID's. Only the paths the
/// server names, with the directories on the way to each (a tool may have made them) and the whole
/// of a directory a copy brought in — never the rest of the home, whose size is the commands'.
/// Commands of other calls are running in this tree while root walks it, which is why the walk
/// holds what it looks at.
pub fn adopt(root: &Path, written: &[String], server_uid: u32, puid: u32, pgid: u32) {
    walk_written(root, written, &mut |entry| {
        if entry.meta.uid() == server_uid {
            let _ = entry.chown(Some(puid), Some(pgid));
        }
    });
}

/// Every entry under `root` on the way to each path, and everything under each path itself, links
/// not followed. A path outside `root`, or one that is not plainly spelled (`..`, `.`), is skipped:
/// the server names these, and the server parses what the agent sends.
pub fn walk_written(root: &Path, written: &[String], visit: &mut dyn FnMut(&Entry)) {
    written.iter().for_each(|path| {
        let Ok(rest) = Path::new(path).strip_prefix(root) else {
            return;
        };
        let names: Option<Vec<&OsStr>> = rest
            .components()
            .map(|component| match component {
                Component::Normal(name) => Some(name),
                _ => None,
            })
            .collect();
        let Some(names) = names.filter(|names| !names.is_empty()) else {
            return;
        };
        let Ok(mut held) = Entry::hold(libc::AT_FDCWD, root.as_os_str(), root.to_path_buf()) else {
            return;
        };
        let last = names.len() - 1;
        for (i, name) in names.into_iter().enumerate() {
            if !held.meta.is_dir() {
                return;
            }
            let Ok(child) = Entry::hold(held.held.as_raw_fd(), name, held.path.join(name)) else {
                return;
            };
            if i == last {
                descend(child, visit);
                return;
            }
            visit(&child);
            held = child;
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    // A command owns the tree being walked, so it can swap a directory for a link to anywhere
    // between the moment the walk looks at it and the moment the walk goes in. Root then acts on
    // whatever the link names — /usr/local/bin, say. The walk must go into the directory it looked
    // at, not into whatever sits at that name a moment later.
    #[test]
    fn a_directory_swapped_for_a_link_mid_walk_is_still_the_one_walked() {
        let tmp = std::env::temp_dir().join(format!("home-walk-{}", std::process::id()));
        let _ = fs::remove_dir_all(&tmp);
        let (root, outside) = (tmp.join("home"), tmp.join("outside"));
        fs::create_dir_all(root.join("d")).unwrap();
        fs::create_dir_all(&outside).unwrap();
        fs::write(root.join("d/inner"), b"").unwrap();
        fs::write(outside.join("victim"), b"").unwrap();
        let swapped = fs::metadata(root.join("d")).unwrap().ino();
        let inner = fs::metadata(root.join("d/inner")).unwrap().ino();
        let victim = fs::metadata(outside.join("victim")).unwrap().ino();

        let mut seen = Vec::new();
        walk(&root, &mut |entry| {
            seen.push(entry.meta.ino());
            if entry.meta.ino() == swapped {
                fs::rename(&entry.path, root.join("d.gone")).unwrap();
                std::os::unix::fs::symlink(&outside, root.join("d")).unwrap();
            }
        });
        let _ = fs::remove_dir_all(&tmp);

        assert!(seen.contains(&inner), "the directory the walk looked at is the one it entered");
        assert!(!seen.contains(&victim), "the walk followed the link put in the directory's place");
    }

    fn tree(name: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!("home-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("a/b/c")).unwrap();
        fs::write(root.join("a/b/c/deep.py"), b"").unwrap();
        fs::write(root.join("a/b/x.py"), b"").unwrap();
        fs::create_dir_all(root.join("big/many")).unwrap();
        fs::write(root.join("big/many/one"), b"").unwrap();
        root
    }

    fn visited(root: &Path, paths: &[&str]) -> Vec<String> {
        let paths: Vec<String> = paths.iter().map(|p| p.replace("{root}", root.to_str().unwrap())).collect();
        let mut seen = Vec::new();
        walk_written(root, &paths, &mut |entry| {
            seen.push(entry.path.strip_prefix(root).unwrap().to_string_lossy().into_owned())
        });
        seen.sort();
        seen
    }

    // Before each command only what the file tools wrote is looked at — the file, the directories
    // the tool may have made on the way to it, and everything under a directory a copy brought
    // in — never the rest of the home, which a command can fill with a hundred thousand entries.
    #[test]
    fn what_was_written_is_walked_with_its_parents_and_nothing_else() {
        let root = tree("written");

        let seen = visited(&root, &["{root}/a/b/x.py", "{root}/a/b/c"]);
        let _ = fs::remove_dir_all(&root);

        assert_eq!(seen, ["a", "a", "a/b", "a/b", "a/b/c", "a/b/c/deep.py", "a/b/x.py"]);
    }

    // A path that has gone since the tool wrote it still has the directories the tool made.
    #[test]
    fn a_path_that_is_gone_still_has_its_parents_walked() {
        let root = tree("gone");

        let seen = visited(&root, &["{root}/a/b/vanished.py"]);
        let _ = fs::remove_dir_all(&root);

        assert_eq!(seen, ["a", "a/b"]);
    }

    // The paths come from the server, which runs as a uid of its own and parses everything the
    // agent sends: nothing outside the home, and no `..` to climb out of it by.
    #[test]
    fn a_path_outside_the_home_or_climbing_out_of_it_is_not_walked() {
        let root = tree("outside");

        let seen = visited(&root, &["/etc/passwd", "{root}/../escape", "{root}/a/../a/b/x.py", "{root}", "relative/x"]);
        let _ = fs::remove_dir_all(&root);

        assert!(seen.is_empty(), "walked {seen:?}");
    }

    // A command owns the tree, so it can put a link where the tool made a directory: the link is
    // looked at (and chowned itself, never what it names) and not gone through.
    #[test]
    fn a_link_on_the_way_is_not_followed() {
        let root = tree("link");
        let outside = std::env::temp_dir().join(format!("home-link-outside-{}", std::process::id()));
        fs::create_dir_all(&outside).unwrap();
        fs::write(outside.join("victim"), b"").unwrap();
        std::os::unix::fs::symlink(&outside, root.join("a/linked")).unwrap();

        let seen = visited(&root, &["{root}/a/linked/victim", "{root}/a/linked"]);
        let _ = fs::remove_dir_all(&root);
        let _ = fs::remove_dir_all(&outside);

        assert_eq!(seen, ["a", "a", "a/linked", "a/linked"]);
    }

    #[test]
    fn a_private_file_becomes_group_writable() {
        assert_eq!(shared_mode(0o644, false), Some(0o664));
    }

    #[test]
    fn an_executable_keeps_its_execute_bit_for_the_group() {
        assert_eq!(shared_mode(0o755, false), Some(0o775));
    }

    #[test]
    fn a_directory_becomes_group_writable_and_setgid() {
        assert_eq!(shared_mode(0o755, true), Some(0o2775));
    }

    #[test]
    fn an_entry_already_shared_is_left_alone() {
        assert_eq!(shared_mode(0o664, false), None);
        assert_eq!(shared_mode(0o2775, true), None);
    }

    #[test]
    fn share_rewrites_the_tree_the_caller_owns() {
        let root = std::env::temp_dir().join(format!("home-share-{}", std::process::id()));
        fs::create_dir_all(root.join("sub")).unwrap();
        fs::write(root.join("sub/notes.md"), "x").unwrap();
        fs::set_permissions(root.join("sub/notes.md"), fs::Permissions::from_mode(0o644)).unwrap();
        fs::set_permissions(root.join("sub"), fs::Permissions::from_mode(0o755)).unwrap();
        let me = fs::metadata(&root).unwrap();

        share(&root, me.uid(), me.uid(), me.gid());

        let file = fs::metadata(root.join("sub/notes.md")).unwrap().mode() & 0o7777;
        let dir = fs::metadata(root.join("sub")).unwrap().mode() & 0o7777;
        fs::remove_dir_all(&root).unwrap();
        assert_eq!(file, 0o664);
        assert_eq!(dir, 0o2775);
    }
}
