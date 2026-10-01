//! The daemon core serving reads: kernel events in, bridge requests out, no kernel and no agent.

mod common;

use common::{FakeBridge, FakeNode};
use sandbox_runtime::vfs::bridge::Kind;
use sandbox_runtime::vfs::core::{Vfs, ROOT};

fn vault() -> Vfs<FakeBridge> {
    Vfs::new(FakeBridge::with(&[
        ("/vault", FakeNode::Dir),
        ("/vault/notes", FakeNode::Dir),
        ("/vault/notes/todo.md", FakeNode::File(b"- TODO milk\n".to_vec(), true)),
        ("/timers", FakeNode::Dir),
        ("/timers/eggs", FakeNode::Dir),
        ("/timers/eggs/status.json", FakeNode::File(br#"{"remainingSeconds":120}"#.to_vec(), false)),
        ("/timers/dismiss", FakeNode::Action),
    ]))
}

fn walk(vfs: &Vfs<FakeBridge>, path: &str) -> u64 {
    path.trim_matches('/')
        .split('/')
        .fold(ROOT, |parent, name| vfs.lookup(parent, name).expect(name).ino)
}

#[test]
fn the_root_lists_the_served_mounts() {
    let vfs = vault();

    let names: Vec<String> = vfs.readdir(ROOT).unwrap().into_iter().map(|e| e.name).collect();

    assert_eq!(names, ["timers", "vault"]);
    assert_eq!(vfs.served().unwrap(), ["timers", "vault"]);
}

#[test]
fn a_disk_file_reports_the_size_the_mount_gives_and_reads_whole() {
    let vfs = vault();
    let ino = walk(&vfs, "/vault/notes/todo.md");

    let node = vfs.getattr(ino).unwrap();
    let opened = vfs.open(ino, false, false).unwrap();

    assert_eq!((node.kind, node.size, node.size_known), (Kind::File, 12, true));
    assert!(!opened.direct_io);
    assert_eq!(vfs.read(opened.fh, 0, 4096).unwrap(), b"- TODO milk\n");
    assert_eq!(vfs.read(opened.fh, 7, 4096).unwrap(), b"milk\n");
}

// A guessed size of zero without direct I/O reads as empty, silently (spike finding 5): a file the
// mount cannot size is opened with direct I/O, read whole, and from then on stats as its length.
#[test]
fn a_rendered_file_of_unknown_size_is_served_with_direct_io_and_never_reads_empty() {
    let vfs = vault();
    let ino = walk(&vfs, "/timers/eggs/status.json");

    let before = vfs.getattr(ino).unwrap();
    let opened = vfs.open(ino, false, false).unwrap();
    let content = vfs.read(opened.fh, 0, 4096).unwrap();
    let after = vfs.getattr(ino).unwrap();

    assert!(!before.size_known);
    assert!(opened.direct_io);
    assert_eq!(content, br#"{"remainingSeconds":120}"#);
    assert_eq!(after.size, content.len() as u64);
}

#[test]
fn a_listing_says_what_each_entry_is() {
    let vfs = vault();
    let timers = walk(&vfs, "/timers");

    let entries: Vec<(String, Kind)> = vfs.readdir(timers).unwrap().into_iter().map(|e| (e.name, e.kind)).collect();

    assert_eq!(entries, [("dismiss".to_string(), Kind::Action), ("eggs".to_string(), Kind::Dir)]);
}

// A shell's PATH search and a `find` probe ask for names that are not there; a listing already
// held answers them without a round trip.
#[test]
fn a_name_the_listing_lacks_is_not_found_without_asking_the_bridge() {
    let vfs = vault();
    let notes = walk(&vfs, "/vault/notes");
    vfs.readdir(notes).unwrap();
    let asked = vfs.bridge().calls().len();

    assert_eq!(vfs.lookup(notes, "missing.md").unwrap_err(), libc::ENOENT);
    assert_eq!(vfs.bridge().calls().len(), asked);
}

#[test]
fn a_missing_path_is_not_found() {
    let vfs = vault();

    assert_eq!(vfs.lookup(ROOT, "laptop").unwrap_err(), libc::ENOENT);
}

#[test]
fn a_directory_cannot_be_opened_as_a_file() {
    let vfs = vault();
    let notes = walk(&vfs, "/vault/notes");

    assert_eq!(vfs.open(notes, false, false).err(), Some(libc::EISDIR));
}
