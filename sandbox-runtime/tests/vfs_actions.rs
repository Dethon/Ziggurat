//! Action files: served as the helper's bytes, executable-only, and run through the daemon with a
//! barrier — whatever the script made so far reaches the mount before the action does.

mod common;

use common::{FakeBridge, FakeNode};
use sandbox_runtime::vfs::bridge::Kind;
use sandbox_runtime::vfs::core::{Vfs, ROOT};

const HELPER: &[u8] = b"\x7fELF-the-helper";

fn mounts() -> Vfs<FakeBridge> {
    Vfs::new(FakeBridge::with(&[
        ("/jobs", FakeNode::Dir),
        ("/jobs/run", FakeNode::Action),
        ("/jobs/existing.txt", FakeNode::File(b"x".to_vec(), true)),
    ]))
    .with_helper(HELPER.to_vec())
}

#[test]
fn an_action_file_is_the_helper_byte_for_byte() {
    let vfs = mounts();
    let jobs = vfs.lookup(ROOT, "jobs").unwrap().ino;
    let run = vfs.lookup(jobs, "run").unwrap();

    let opened = vfs.open(run.ino, false, false).unwrap();

    assert_eq!((run.kind, run.size), (Kind::Action, HELPER.len() as u64));
    assert_eq!(vfs.read(opened.fh, 0, 4096).unwrap(), HELPER);
}

#[test]
fn an_action_file_cannot_be_written() {
    let vfs = mounts();
    let jobs = vfs.lookup(ROOT, "jobs").unwrap().ino;
    let run = vfs.lookup(jobs, "run").unwrap();

    assert_eq!(vfs.open(run.ino, true, false).err(), Some(libc::EACCES));
}

// A file the script made a moment ago is on the mount by the time the action runs.
#[test]
fn an_action_commits_everything_held_first() {
    let vfs = mounts();
    let jobs = vfs.lookup(ROOT, "jobs").unwrap().ino;
    let (_, fh) = vfs.create(jobs, "input.txt").unwrap();
    vfs.write(fh, 0, b"prepared").unwrap();
    vfs.release(fh);

    let output = vfs.action("/jobs/run", &["--fast".to_string()]).unwrap();

    assert_eq!(
        vfs.bridge().mutations(),
        ["write /jobs/input.txt create prepared", "action /jobs/run --fast"]
    );
    assert!(output.stdout.contains("input.txt"), "{}", output.stdout);
}
