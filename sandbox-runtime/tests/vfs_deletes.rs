//! Deletes and moves: unlink and rmdir are held like new files, so `rm -r` of a directory — which
//! unlinks every child before it removes the directory — reaches the mount as the one delete the
//! remove tool makes. A rename of something on the mount is the bridge's to turn into the mount's
//! move or a transfer.

mod common;

use common::{FakeBridge, FakeNode};
use sandbox_runtime::vfs::core::{Vfs, ROOT};

fn mounts() -> Vfs<FakeBridge> {
    Vfs::new(FakeBridge::with(&[
        ("/timers", FakeNode::Dir),
        ("/timers/eggs", FakeNode::Dir),
        ("/timers/eggs/timer.json", FakeNode::File(b"{}".to_vec(), false)),
        ("/timers/eggs/status.json", FakeNode::File(b"{}".to_vec(), false)),
        ("/vault", FakeNode::Dir),
        ("/vault/a.md", FakeNode::File(b"a\n".to_vec(), true)),
        ("/vault/b.md", FakeNode::File(b"b\n".to_vec(), true)),
        ("/vault/sub", FakeNode::Dir),
        ("/notes", FakeNode::Dir),
    ]))
}

fn ino(vfs: &Vfs<FakeBridge>, path: &str) -> u64 {
    path.trim_matches('/').split('/').fold(ROOT, |parent, name| vfs.lookup(parent, name).expect(name).ino)
}

fn names(vfs: &Vfs<FakeBridge>, path: &str) -> Vec<String> {
    vfs.readdir(ino(vfs, path)).unwrap().into_iter().map(|e| e.name).collect()
}

#[test]
fn rm_r_of_a_directory_is_one_delete_of_the_directory() {
    let vfs = mounts();
    let eggs = ino(&vfs, "/timers/eggs");
    let timers = ino(&vfs, "/timers");

    names(&vfs, "/timers/eggs").iter().for_each(|n| vfs.unlink(eggs, n).unwrap());
    vfs.rmdir(timers, "eggs").unwrap();
    let before_exit = vfs.bridge().mutations();
    vfs.finish();

    assert!(before_exit.is_empty(), "{before_exit:?}");
    assert_eq!(vfs.bridge().mutations(), ["delete /timers/eggs"]);
}

#[test]
fn a_deleted_path_is_gone_for_the_rest_of_the_command() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");

    vfs.unlink(vault, "a.md").unwrap();

    assert_eq!(vfs.lookup(vault, "a.md").unwrap_err(), libc::ENOENT);
    assert_eq!(names(&vfs, "/vault"), ["b.md", "sub"]);
}

#[test]
fn rm_of_a_file_commits_when_the_command_ends() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");

    vfs.unlink(vault, "a.md").unwrap();
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), ["delete /vault/a.md"]);
}

// A directory whose children are still there is not empty, as on any filesystem.
#[test]
fn rmdir_of_a_directory_with_children_is_refused() {
    let vfs = mounts();
    let timers = ino(&vfs, "/timers");

    assert_eq!(vfs.rmdir(timers, "eggs").unwrap_err(), libc::ENOTEMPTY);
}

// `rm f; echo x > f`: the delete and the new file both reach the mount, delete first.
#[test]
fn a_file_made_where_one_was_deleted_commits_after_the_delete() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");

    vfs.unlink(vault, "a.md").unwrap();
    let (_, fh) = vfs.create(vault, "a.md").unwrap();
    vfs.write(fh, 0, b"again\n").unwrap();
    vfs.release(fh);
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), ["delete /vault/a.md", "write /vault/a.md create again\n"]);
}

// A rename inside one mount arrives as one rename, never a delete and a create.
#[test]
fn mv_within_a_mount_is_the_bridges_rename() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    let sub = ino(&vfs, "/vault/sub");

    vfs.rename(vault, "a.md", sub, "a.md").unwrap();

    assert_eq!(vfs.bridge().mutations(), ["rename /vault/a.md /vault/sub/a.md"]);
    assert_eq!(vfs.lookup(vault, "a.md").unwrap_err(), libc::ENOENT);
    assert!(vfs.lookup(sub, "a.md").is_ok());
}

// Both mounts sit in one filesystem, so a move between them is one rename too; the bridge makes
// it a transfer.
#[test]
fn mv_between_mounts_is_one_rename() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    let notes = ino(&vfs, "/notes");

    vfs.rename(vault, "a.md", notes, "a.md").unwrap();

    assert_eq!(vfs.bridge().mutations(), ["rename /vault/a.md /notes/a.md"]);
}

// Onto an existing path, the bridge is told it overwrites: it judges that as a write there.
#[test]
fn mv_onto_an_existing_file_says_it_overwrites() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");

    vfs.rename(vault, "a.md", vault, "b.md").unwrap();

    assert_eq!(vfs.bridge().mutations(), ["rename /vault/a.md /vault/b.md overwrite"]);
}

// A refused move leaves both paths as they were.
#[test]
fn a_refused_mv_leaves_the_source_where_it_was() {
    let vfs = mounts();
    vfs.bridge().refuse("/vault/a.md");
    let vault = ino(&vfs, "/vault");
    let notes = ino(&vfs, "/notes");

    assert_eq!(vfs.rename(vault, "a.md", notes, "a.md").unwrap_err(), libc::EACCES);

    assert!(vfs.lookup(vault, "a.md").is_ok());
    assert_eq!(vfs.lookup(notes, "a.md").unwrap_err(), libc::ENOENT);
}

// `rm -r d && mkdir d`: a deleted directory is not there for the rest of the command, so making
// it again is not refused as existing.
#[test]
fn a_directory_made_where_one_was_deleted_is_not_refused_as_existing() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    let sub = ino(&vfs, "/vault/sub");
    names(&vfs, "/vault/sub").iter().for_each(|n| vfs.unlink(sub, n).unwrap());
    vfs.rmdir(vault, "sub").unwrap();

    assert!(vfs.mkdir(vault, "sub").is_ok());
}

// `rm f; echo x > t; mv t f`: the rename commits now, so the delete it replaces must commit first
// — never afterwards, which would remove what the command just wrote.
#[test]
fn a_rename_onto_a_deleted_path_lands_after_the_delete_and_survives_the_end() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    vfs.unlink(vault, "a.md").unwrap();
    let (_, fh) = vfs.create(vault, "t").unwrap();
    vfs.write(fh, 0, b"x\n").unwrap();
    vfs.release(fh);

    vfs.rename(vault, "t", vault, "a.md").unwrap();
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), ["delete /vault/a.md", "write /vault/a.md create x\n"]);
    assert_eq!(vfs.bridge().content("/vault/a.md").as_deref(), Some("x\n"));
}

// A file committed by a rename inside a directory the command made: the directory now exists on
// the mount, and the file is visible for the rest of the command.
#[test]
fn a_file_committed_inside_a_new_directory_stays_visible() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    let dir = vfs.mkdir(vault, "new").unwrap().ino;
    let (_, fh) = vfs.create(dir, ".tmp").unwrap();
    vfs.write(fh, 0, b"y").unwrap();
    vfs.release(fh);

    vfs.rename(dir, ".tmp", dir, "note.md").unwrap();

    assert!(vfs.lookup(dir, "note.md").is_ok());
}

// `mkdir Aprendizaje && mv a.md b.md Aprendizaje/`: the mount's move brings the new directory
// into being, so it is the mount's from then on — what was moved into it is there to stat, the
// next move lands beside it, and a listing shows both. Held, it answered every name under it
// as missing, and GNU mv reported a move the mount had made as "cannot stat".
#[test]
fn a_file_moved_into_a_new_directory_is_there_for_the_rest_of_the_command() {
    let vfs = mounts();
    let vault = ino(&vfs, "/vault");
    let dir = vfs.mkdir(vault, "new").unwrap().ino;

    vfs.rename(vault, "a.md", dir, "a.md").unwrap();
    vfs.rename(vault, "b.md", dir, "b.md").unwrap();

    assert!(vfs.lookup(dir, "a.md").is_ok());
    assert!(vfs.lookup(dir, "b.md").is_ok());
    assert_eq!(names(&vfs, "/vault/new"), ["a.md", "b.md"]);
    vfs.finish();
    assert_eq!(
        vfs.bridge().mutations(),
        ["rename /vault/a.md /vault/new/a.md", "rename /vault/b.md /vault/new/b.md"]
    );
}
