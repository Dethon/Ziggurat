//! Becoming somebody else, between fork and exec. Everything here runs in a forked child of a
//! process that may have other threads, so it is raw syscalls on memory prepared before the fork:
//! no allocation, no locks, no formatting. The vfs daemon is the one caller that is not a forked
//! child: it drops in place, having checked it is still a single thread.

use std::ffi::CString;
use std::io;

/// Who a child becomes. Built before the fork, so the child only reads it.
pub struct Identity {
    pub uid: libc::uid_t,
    pub gid: libc::gid_t,
    pub groups: Vec<libc::gid_t>,
    /// Applied after the drop, so a path only the new identity can enter is entered as it.
    pub cwd: Option<CString>,
    pub umask: libc::mode_t,
}

fn check(result: libc::c_int) -> io::Result<()> {
    if result == -1 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

/// Drops to `identity` for good: groups, gid, the whole capability bounding set, then uid, then
/// no-new-privs. The bounding set is load-bearing, not tidiness: the container holds SYS_ADMIN for
/// FUSE, and a child keeps the container's bounding set, so a setuid-root binary run later would
/// get it back (spike finding 1). No-new-privs closes the same door for every setuid binary at
/// once, whatever the image happens to ship. Last, the syscall filter (`seccomp`): no user
/// namespace, which needs no capability to make and is root inside.
///
/// # Safety
/// Call only in a process with exactly one thread: between fork and exec, as
/// `CommandExt::pre_exec` does, or in a process that has counted its own threads first, as the vfs
/// daemon does (`give_up_root`). Capabilities, no-new-privs and the syscall filter are per thread,
/// so a second thread would keep what this one gave up.
pub unsafe fn become_identity(identity: &Identity) -> io::Result<()> {
    unsafe {
        check(libc::setgroups(identity.groups.len(), identity.groups.as_ptr()))?;
        check(libc::setgid(identity.gid))?;
        // Capabilities past the kernel's last one answer EINVAL, which is the end of the set.
        (0..64).for_each(|cap| {
            libc::prctl(libc::PR_CAPBSET_DROP, cap as libc::c_ulong, 0, 0, 0);
        });
        check(libc::prctl(libc::PR_CAP_AMBIENT, libc::PR_CAP_AMBIENT_CLEAR_ALL as libc::c_ulong, 0, 0, 0))?;
        // From root to anyone else, setuid clears the permitted and effective sets.
        check(libc::setuid(identity.uid))?;
        check(libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0))?;
        crate::seccomp::refuse_user_namespaces()?;
        libc::umask(identity.umask);
        if let Some(cwd) = &identity.cwd {
            check(libc::chdir(cwd.as_ptr()))?;
        }
    }
    Ok(())
}

/// The supplementary groups a command keeps: whatever the container was started with (compose's
/// `group_add`, which is how `render` reaches the GPU), minus root's own group, plus PGID.
pub fn kept_groups(pgid: libc::gid_t) -> io::Result<Vec<libc::gid_t>> {
    let count = unsafe { libc::getgroups(0, std::ptr::null_mut()) };
    check(count)?;
    let mut groups = vec![0 as libc::gid_t; count as usize];
    let read = unsafe { libc::getgroups(count, groups.as_mut_ptr()) };
    check(read)?;
    groups.truncate(read as usize);
    let mut kept: Vec<libc::gid_t> = groups.into_iter().filter(|&g| g != 0 && g != pgid).collect();
    kept.insert(0, pgid);
    Ok(kept)
}
