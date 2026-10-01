//! The home volume is shared by two uids: commands run as PUID, which owns it, and the sandbox
//! MCP server runs as its own uid so a command cannot read, trace or kill it. The file tools run
//! inside that server, so the server has to reach the volume too. It does so as a member of PGID:
//! the volume is kept group-writable (directories setgid, so new entries stay in PGID), commands
//! and the server both run with umask 002, and whatever the server created is handed to PUID
//! before each command, so a command can `chmod +x` a script the file tool wrote.
//!
//! What this cannot reach: a file a command deliberately creates 0600 (an ssh key, mkstemp) stays
//! unreadable to the file tools. Accepted; such files are a command's own business.

use std::fs;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::Path;

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

/// Every entry under `root`, links not followed, `root` included. Unreadable subtrees are skipped:
/// a permissions pass must never be the reason the container does not start.
pub fn walk(root: &Path, visit: &mut dyn FnMut(&Path, &fs::Metadata)) {
    let Ok(meta) = fs::symlink_metadata(root) else {
        return;
    };
    visit(root, &meta);
    if !meta.is_dir() {
        return;
    }
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    entries.filter_map(Result::ok).for_each(|e| walk(&e.path(), visit));
}

/// At boot: make the volume group-shared. PUID's own entries also move into PGID, which is what an
/// entry made before this layout existed may lack.
pub fn share(root: &Path, puid: u32, pgid: u32) {
    walk(root, &mut |path, meta| {
        if meta.file_type().is_symlink() {
            return;
        }
        if meta.uid() == puid && meta.gid() != pgid {
            let _ = std::os::unix::fs::lchown(path, None, Some(pgid));
        }
        if let Some(mode) = shared_mode(meta.mode() & 0o7777, meta.is_dir()) {
            let _ = fs::set_permissions(path, fs::Permissions::from_mode(mode));
        }
    });
}

/// Before each command: whatever the server created becomes PUID's.
pub fn adopt(root: &Path, server_uid: u32, puid: u32, pgid: u32) {
    walk(root, &mut |path, meta| {
        if meta.uid() == server_uid {
            let _ = std::os::unix::fs::lchown(path, Some(puid), Some(pgid));
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

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

        share(&root, me.uid(), me.gid());

        let file = fs::metadata(root.join("sub/notes.md")).unwrap().mode() & 0o7777;
        let dir = fs::metadata(root.join("sub")).unwrap().mode() & 0o7777;
        fs::remove_dir_all(&root).unwrap();
        assert_eq!(file, 0o664);
        assert_eq!(dir, 0o2775);
    }
}
