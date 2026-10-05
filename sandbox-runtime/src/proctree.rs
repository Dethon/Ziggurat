//! Which processes a kill has to reach. The unit is a child subreaper, so every descendant a
//! command leaves behind — a backgrounded job, a double-forked daemon — is reparented to it rather
//! than escaping to the container's init, and the whole tree is the unit's descendants.

use std::collections::{HashMap, HashSet};

/// Every descendant of `root`, given each process's parent. `root` itself is not included.
pub fn descendants(root: i32, parents: &HashMap<i32, i32>) -> HashSet<i32> {
    let mut found = HashSet::new();
    let mut frontier = vec![root];
    while let Some(parent) = frontier.pop() {
        let children: Vec<i32> = parents
            .iter()
            .filter(|&(&pid, &ppid)| ppid == parent && pid != root && !found.contains(&pid))
            .map(|(&pid, _)| pid)
            .collect();
        found.extend(children.iter().copied());
        frontier.extend(children);
    }
    found
}

/// Whether `pid` is `ancestor` or one of its descendants, following `parent_of` up the tree.
pub fn descends_from(pid: i32, ancestor: i32, parent_of: impl Fn(i32) -> Option<i32>) -> bool {
    std::iter::successors(Some(pid), |&p| parent_of(p).filter(|&parent| parent > 0 && parent != p))
        .take(256)
        .any(|p| p == ancestor)
}

/// The parent of every process `/proc` can see. A process that exits mid-read is skipped.
#[cfg(target_os = "linux")]
pub fn read_parents() -> HashMap<i32, i32> {
    let Ok(entries) = std::fs::read_dir("/proc") else {
        return HashMap::new();
    };
    entries
        .filter_map(Result::ok)
        .filter_map(|e| e.file_name().to_str()?.parse::<i32>().ok())
        .filter_map(|pid| Some((pid, parent_of(pid)?)))
        .collect()
}

// `/proc/<pid>/stat` is "pid (comm) state ppid ...", and comm may itself contain spaces and
// parentheses, so the fields are counted from the last closing parenthesis.
#[cfg(target_os = "linux")]
pub fn parent_of(pid: i32) -> Option<i32> {
    let stat = std::fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    parse_ppid(&stat)
}

/// Who is on the other end of a unix socket, as the kernel recorded it at connect: nothing the peer
/// sends can change it.
#[cfg(target_os = "linux")]
pub fn peer(connection: &std::os::unix::net::UnixStream) -> Option<libc::ucred> {
    use std::os::fd::AsRawFd;

    let mut credentials = libc::ucred { pid: 0, uid: 0, gid: 0 };
    let mut length = std::mem::size_of::<libc::ucred>() as libc::socklen_t;
    let result = unsafe {
        libc::getsockopt(
            connection.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_PEERCRED,
            &mut credentials as *mut _ as *mut libc::c_void,
            &mut length,
        )
    };
    (result == 0).then_some(credentials)
}

pub fn parse_ppid(stat: &str) -> Option<i32> {
    let after_comm = &stat[stat.rfind(')')? + 1..];
    after_comm.split_whitespace().nth(1)?.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn descendants_reach_grandchildren_and_skip_strangers() {
        let parents = HashMap::from([(10, 1), (11, 10), (12, 11), (13, 11), (20, 1), (21, 20)]);

        let found = descendants(10, &parents);

        assert_eq!(found, HashSet::from([11, 12, 13]));
    }

    #[test]
    fn a_descendant_is_found_up_its_chain_and_a_stranger_is_not() {
        let parents = HashMap::from([(30, 20), (20, 10), (10, 1), (40, 1)]);
        let parent_of = |pid| parents.get(&pid).copied();

        assert!(descends_from(30, 10, parent_of));
        assert!(descends_from(10, 10, parent_of));
        assert!(!descends_from(40, 10, parent_of));
    }

    #[test]
    fn the_ppid_is_read_past_a_comm_with_spaces_and_parens() {
        let stat = "4242 (my (weird) cmd) S 4100 4242 4242 0 -1 4194560";

        assert_eq!(parse_ppid(stat), Some(4100));
    }
}
