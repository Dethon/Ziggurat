//! The one syscall filter a command runs under, installed by the unit after no-new-privs. Docker
//! picks its own filter per container, and once the container holds SYS_ADMIN (for FUSE) that
//! filter lets every process in it make namespaces, including a command running with no
//! capabilities at all. A user namespace needs none to make and is root inside: the usual first
//! step of a kernel escalation, and refused on a container without SYS_ADMIN. This puts it back,
//! on every host, whatever AppArmor the host has.

use std::io;

// seccomp_data: the syscall number, then the arch, then the arguments from byte 16, so the low half
// of the first argument (little-endian) is at 16.
const NR: u32 = 0;
const ARCH: u32 = 4;
const ARG0_LOW: u32 = 16;
// The ABI this binary was built for, which is the one whose syscall numbers `libc::SYS_*` are. The
// image builds the launcher for the machine it is built on (`$(uname -m)` in the Dockerfile), so a
// filter that named one architecture answered ENOSYS to every syscall — execve first — on the
// other. A third architecture does not build, rather than build a filter that refuses everything.
#[cfg(target_arch = "x86_64")]
const AUDIT_ARCH_NATIVE: u32 = 0xc000_003e;
#[cfg(target_arch = "aarch64")]
const AUDIT_ARCH_NATIVE: u32 = 0xc000_00b7;
#[cfg(not(any(target_arch = "x86_64", target_arch = "aarch64")))]
compile_error!("seccomp.rs names the syscall ABI it filters; add this architecture's AUDIT_ARCH to it");

// x86_64's second ABI in the same architecture word. No aarch64 syscall number reaches it, so the
// check is inert there and the filter keeps one shape.
const X32_SYSCALL_BIT: u32 = 0x4000_0000;

const fn statement(code: u32, k: u32) -> libc::sock_filter {
    libc::sock_filter { code: code as u16, jt: 0, jf: 0, k }
}

const fn jump(code: u32, k: u32, jt: u8, jf: u8) -> libc::sock_filter {
    libc::sock_filter { code: code as u16, jt, jf, k }
}

const LOAD: u32 = libc::BPF_LD | libc::BPF_W | libc::BPF_ABS;
const JEQ: u32 = libc::BPF_JMP | libc::BPF_JEQ | libc::BPF_K;
const JGE: u32 = libc::BPF_JMP | libc::BPF_JGE | libc::BPF_K;
const JSET: u32 = libc::BPF_JMP | libc::BPF_JSET | libc::BPF_K;
const RET: u32 = libc::BPF_RET | libc::BPF_K;

// Jumps count the statements they skip. Another ABI (i386 by `int 0x80`, x32, 32-bit arm) is
// ENOSYS whole: its numbers differ, and the image ships no binary that uses one. clone3 is ENOSYS too, as
// Docker answers it on a container without SYS_ADMIN, because its flags sit behind a pointer the
// filter cannot follow; glibc then falls back to clone, whose flags it can read.
static FILTER: [libc::sock_filter; 12] = [
    /* 0 */ statement(LOAD, ARCH),
    /* 1 */ jump(JEQ, AUDIT_ARCH_NATIVE, 0, 9),
    /* 2 */ statement(LOAD, NR),
    /* 3 */ jump(JGE, X32_SYSCALL_BIT, 7, 0),
    /* 4 */ jump(JEQ, libc::SYS_clone3 as u32, 6, 0),
    /* 5 */ jump(JEQ, libc::SYS_unshare as u32, 1, 0),
    /* 6 */ jump(JEQ, libc::SYS_clone as u32, 0, 2),
    /* 7 */ statement(LOAD, ARG0_LOW),
    /* 8 */ jump(JSET, libc::CLONE_NEWUSER as u32, 1, 0),
    /* 9 */ statement(RET, libc::SECCOMP_RET_ALLOW),
    /* 10 */ statement(RET, libc::SECCOMP_RET_ERRNO | libc::EPERM as u32),
    /* 11 */ statement(RET, libc::SECCOMP_RET_ERRNO | libc::ENOSYS as u32),
];

/// Refuses a new user namespace, by `unshare` or `clone`, with EPERM.
///
/// # Safety
/// Call only between fork and exec, after no-new-privs: the filter is static, so nothing here
/// allocates.
pub unsafe fn refuse_user_namespaces() -> io::Result<()> {
    let program = libc::sock_fprog { len: FILTER.len() as u16, filter: FILTER.as_ptr() as *mut libc::sock_filter };
    let installed = unsafe {
        libc::prctl(libc::PR_SET_SECCOMP, libc::SECCOMP_MODE_FILTER, &program as *const libc::sock_fprog, 0, 0)
    };
    if installed == -1 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    // Each check is its own exit code, so a failure names what got through.
    fn in_a_filtered_child() -> i32 {
        unsafe {
            let pid = libc::fork();
            assert!(pid >= 0, "fork");
            if pid == 0 {
                if libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) == -1 || refuse_user_namespaces().is_err() {
                    libc::_exit(10);
                }
                if libc::unshare(libc::CLONE_NEWUSER) != -1 || *libc::__errno_location() != libc::EPERM {
                    libc::_exit(11);
                }
                // clone3's flags are behind a pointer; ENOSYS sends glibc back to clone.
                if libc::syscall(libc::SYS_clone3, std::ptr::null::<u8>(), 0usize) != -1
                    || *libc::__errno_location() != libc::ENOSYS
                {
                    libc::_exit(12);
                }
                let grandchild = libc::fork();
                if grandchild == 0 {
                    libc::_exit(0);
                }
                let mut status = 0;
                if grandchild < 0 || libc::waitpid(grandchild, &mut status, 0) != grandchild || status != 0 {
                    libc::_exit(13);
                }
                libc::_exit(0);
            }
            let mut status = 0;
            libc::waitpid(pid, &mut status, 0);
            assert!(libc::WIFEXITED(status), "the child died of a signal: {status}");
            libc::WEXITSTATUS(status)
        }
    }

    #[test]
    fn a_filtered_process_cannot_make_a_user_namespace_but_still_forks() {
        assert_eq!(in_a_filtered_child(), 0);
    }
}
