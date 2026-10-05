//! The commit state machine: what a command's writes become, and when. Kernel events go in as the
//! kernel sends them — including the double flush and the late release — and the bridge's
//! mutations come out. Spike findings 5 (flush is not commit, `sed -i` renames over) are the cases.

mod common;

use common::{FakeBridge, FakeNode};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use sandbox_runtime::vfs::core::{Vfs, ROOT};

const NOTE: &str = "/vault/notes/todo.md";

fn vault() -> Vfs<FakeBridge> {
    Vfs::new(FakeBridge::with(&[
        ("/vault", FakeNode::Dir),
        ("/vault/notes", FakeNode::Dir),
        (NOTE, FakeNode::File(b"old\n".to_vec(), true)),
    ]))
}

fn ino(vfs: &Vfs<FakeBridge>, path: &str) -> u64 {
    path.trim_matches('/').split('/').fold(ROOT, |parent, name| vfs.lookup(parent, name).expect(name).ino)
}

fn read_all(vfs: &Vfs<FakeBridge>, path: &str) -> String {
    let opened = vfs.open(ino(vfs, path), false, false).unwrap();
    let bytes = vfs.read(opened.fh, 0, 1 << 20).unwrap();
    vfs.release(opened.fh);
    String::from_utf8(bytes).unwrap()
}

// `echo text > /vault/new.md`: the file is held until the command ends, so the empty file the
// open makes and the double flush bash's dup produces never reach the mount.
#[test]
fn a_new_file_is_held_and_commits_once_when_the_command_ends() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");

    let (_, fh) = vfs.create(notes, "new.md").unwrap();
    vfs.write(fh, 0, b"text\n").unwrap();
    vfs.flush(fh);
    vfs.flush(fh);
    vfs.release(fh);
    let before_exit = vfs.bridge().mutations();
    vfs.finish();

    assert!(before_exit.is_empty(), "{before_exit:?}");
    assert_eq!(vfs.bridge().mutations(), ["write /vault/notes/new.md create text\n"]);
}

// An existing file commits on its last release, never on a flush.
#[test]
fn an_existing_file_commits_once_on_its_last_release() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    let first = vfs.open(note, true, true).unwrap();
    let second = vfs.open(note, true, false).unwrap();
    vfs.write(first.fh, 0, b"new\n").unwrap();
    vfs.flush(first.fh);
    vfs.release(first.fh);
    let after_first = vfs.bridge().mutations();
    vfs.flush(second.fh);
    vfs.release(second.fh);

    assert!(after_first.is_empty(), "{after_first:?}");
    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite new\n")]);
}

// Opened without truncation, the file starts from what the mount has: `>>` appends.
#[test]
fn an_append_writes_after_the_existing_content() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    let opened = vfs.open(note, true, false).unwrap();
    let size = vfs.getattr(note).unwrap().size;
    vfs.write(opened.fh, size, b"more\n").unwrap();
    vfs.release(opened.fh);

    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite old\nmore\n")]);
}

#[test]
fn a_write_open_that_writes_nothing_commits_nothing() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    let opened = vfs.open(note, true, false).unwrap();
    vfs.release(opened.fh);

    assert!(vfs.bridge().mutations().is_empty());
}

// `open(f, 'w')` and nothing written still empties the file.
#[test]
fn a_truncating_open_commits_even_with_nothing_written() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    let opened = vfs.open(note, true, true).unwrap();
    vfs.release(opened.fh);

    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite ")]);
}

// `sed -i`: a temp file in the same directory, renamed over the note. One write to the note; the
// temp never reaches the mount.
#[test]
fn a_held_file_renamed_over_an_existing_one_is_a_write_to_it() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");

    let (_, fh) = vfs.create(notes, "sedAbC123").unwrap();
    vfs.write(fh, 0, b"edited\n").unwrap();
    vfs.setattr(ino(&vfs, "/vault/notes/sedAbC123"), None).unwrap();
    vfs.release(fh);
    vfs.rename(notes, "sedAbC123", notes, "todo.md").unwrap();
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite edited\n")]);
    assert_eq!(vfs.lookup(notes, "sedAbC123").unwrap_err(), libc::ENOENT);
    assert_eq!(read_all(&vfs, NOTE), "edited\n");
}

#[test]
fn a_held_file_renamed_onto_a_new_name_creates_it() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");

    let (_, fh) = vfs.create(notes, ".tmp").unwrap();
    vfs.write(fh, 0, b"x").unwrap();
    vfs.release(fh);
    vfs.rename(notes, ".tmp", notes, "final.md").unwrap();

    assert_eq!(vfs.bridge().mutations(), ["write /vault/notes/final.md create x"]);
}

// `mkdir -p a/b && echo x > a/b/c.md`: the directories exist only in the call until a file lands
// under them, and then arrive with it.
#[test]
fn a_held_directory_arrives_with_the_first_file_under_it() {
    let vfs = vault();
    let vault_ino = ino(&vfs, "/vault");

    let a = vfs.mkdir(vault_ino, "a").unwrap().ino;
    let b = vfs.mkdir(a, "b").unwrap().ino;
    let (_, fh) = vfs.create(b, "c.md").unwrap();
    vfs.write(fh, 0, b"x\n").unwrap();
    vfs.release(fh);
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), ["write /vault/a/b/c.md create x\n"]);
}

#[test]
fn an_empty_mkdir_leaves_nothing_on_the_mount() {
    let vfs = vault();
    let vault_ino = ino(&vfs, "/vault");

    vfs.mkdir(vault_ino, "empty").unwrap();
    vfs.finish();

    assert!(vfs.bridge().mutations().is_empty());
}

// What the command made is there for the rest of the command, held or not.
#[test]
fn a_held_file_is_listed_and_read_back_before_it_commits() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");

    let (_, fh) = vfs.create(notes, "draft.md").unwrap();
    vfs.write(fh, 0, b"draft\n").unwrap();
    vfs.release(fh);

    let names: Vec<String> = vfs.readdir(notes).unwrap().into_iter().map(|e| e.name).collect();
    assert_eq!(names, ["draft.md", "todo.md"]);
    assert_eq!(read_all(&vfs, "/vault/notes/draft.md"), "draft\n");
    assert_eq!(vfs.getattr(ino(&vfs, "/vault/notes/draft.md")).unwrap().size, 6);
}

// A refused write leaves the mount as it was, and the command reads back what the mount has.
#[test]
fn a_refused_commit_reads_back_as_the_mount_has_it() {
    let vfs = vault();
    vfs.bridge().refuse(NOTE);
    let note = ino(&vfs, NOTE);
    assert_eq!(read_all(&vfs, NOTE), "old\n");

    let opened = vfs.open(note, true, true).unwrap();
    vfs.write(opened.fh, 0, b"nope\n").unwrap();
    vfs.release(opened.fh);

    assert_eq!(read_all(&vfs, NOTE), "old\n");
}

// A write through the bridge, then a read in the same command: the read sees the new content.
#[test]
fn a_committed_write_is_what_the_next_read_sees() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);
    assert_eq!(read_all(&vfs, NOTE), "old\n");

    let opened = vfs.open(note, true, true).unwrap();
    vfs.write(opened.fh, 0, b"fresh\n").unwrap();
    vfs.release(opened.fh);

    assert_eq!(read_all(&vfs, NOTE), "fresh\n");
}

// `truncate -s 0 note` with no open: the truncation is the write.
#[test]
fn truncating_a_closed_file_commits_the_shortened_content() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    vfs.setattr(note, Some(1)).unwrap();

    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite o")]);
}

// A mode, owner or time change (sed's fchmod on its temp, `cp -p`) is accepted and changes
// nothing: whether a write is allowed is the bridge's decision, never the mode's.
#[test]
fn a_mode_or_time_change_is_accepted_and_commits_nothing() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);

    vfs.setattr(note, None).unwrap();

    assert!(vfs.bridge().mutations().is_empty());
}

// The kernel releases a killed or exited command's open files after the process is gone, so the
// end of the command waits for them before committing what it holds.
#[test]
fn the_end_of_the_command_waits_for_a_late_release() {
    let vfs = Arc::new(vault());
    let note = ino(&vfs, NOTE);
    let opened = vfs.open(note, true, true).unwrap();
    vfs.write(opened.fh, 0, b"late\n").unwrap();

    let finishing = {
        let vfs = Arc::clone(&vfs);
        thread::spawn(move || vfs.finish())
    };
    // An absence is bought with time: long enough for a finish that does not wait to return.
    thread::sleep(Duration::from_millis(300));
    let finished_early = finishing.is_finished();
    vfs.release(opened.fh);
    finishing.join().unwrap();

    assert!(!finished_early, "finish returned while the command's file was still open");
    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite late\n")]);
}

// The launcher revokes before it kills: the kernel then releases the dead command's open file and
// the daemon commits it, and the bridge — revoked — drops it, as it drops whatever was held.
#[test]
fn after_a_revocation_every_commit_still_reaches_the_bridge_to_be_dropped() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);
    let notes = ino(&vfs, "/vault/notes");
    let opened = vfs.open(note, true, true).unwrap();
    vfs.write(opened.fh, 0, b"half").unwrap();
    let (_, held) = vfs.create(notes, "new.md").unwrap();
    vfs.release(held);

    vfs.revoke();
    vfs.release(opened.fh);
    vfs.finish();

    assert_eq!(
        vfs.bridge().mutations(),
        [
            "revoke".to_string(),
            format!("dropped write {NOTE} overwrite half"),
            "dropped write /vault/notes/new.md create ".to_string()
        ]
    );
    assert_eq!(vfs.bridge().content(NOTE).unwrap(), "old\n");
}

// The daemon holds a file whole, in memory, and the size is the command's to name: `truncate -s 1T`
// or one write at a far offset must be refused as too large, not attempted — an allocation that
// fails aborts the daemon and takes the mount with it.
#[test]
fn a_size_past_the_bound_is_refused_not_allocated() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");
    let note = ino(&vfs, NOTE);
    let (node, held) = vfs.create(notes, "big.bin").unwrap();
    let opened = vfs.open(note, true, false).unwrap();

    assert_eq!(vfs.setattr(node.ino, Some(1 << 40)).unwrap_err(), libc::EFBIG);
    assert_eq!(vfs.write(held, 1 << 40, b"x").unwrap_err(), libc::EFBIG);
    assert_eq!(vfs.setattr(note, Some(1 << 40)).unwrap_err(), libc::EFBIG);
    assert_eq!(vfs.write(opened.fh, u64::MAX, b"x").unwrap_err(), libc::EFBIG);
    vfs.release(opened.fh);
    assert_eq!(vfs.setattr(note, Some(1 << 40)).unwrap_err(), libc::EFBIG);
    assert!(vfs.bridge().mutations().is_empty());
}

// The revocation never reached the agent — it was down, or answered too late. The daemon is then
// the only one who knows the command was revoked, and an agent that never heard would apply
// whatever the kill flushes: so the daemon sends nothing more.
#[test]
fn a_revocation_the_bridge_never_heard_still_commits_nothing() {
    let vfs = vault();
    let note = ino(&vfs, NOTE);
    let notes = ino(&vfs, "/vault/notes");
    let opened = vfs.open(note, true, true).unwrap();
    vfs.write(opened.fh, 0, b"half").unwrap();
    let (_, held) = vfs.create(notes, "new.md").unwrap();
    vfs.release(held);
    *vfs.bridge().revocation_fails.lock().unwrap() = true;

    vfs.revoke();
    vfs.release(opened.fh);
    assert_eq!(vfs.rename(notes, "todo.md", notes, "moved.md"), Err(libc::EACCES));
    assert_eq!(vfs.action("/vault/notes/run", &[]).unwrap_err(), libc::EACCES);
    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), ["revoke"]);
    assert_eq!(vfs.bridge().content(NOTE).unwrap(), "old\n");
}

// The last release commits after it lets go of the writer, and the kernel sends it on its own
// thread after the command has exited. The command is not over until that commit has reached the
// bridge: exec answers once finish returns, and a change log read then must already hold it.
#[test]
fn the_command_does_not_finish_while_a_release_is_still_committing() {
    let vfs = Arc::new(vault());
    vfs.bridge().hold_writes();
    let opened = vfs.open(ino(&vfs, NOTE), true, true).unwrap();
    vfs.write(opened.fh, 0, b"new\n").unwrap();

    let releasing = {
        let vfs = Arc::clone(&vfs);
        thread::spawn(move || vfs.release(opened.fh))
    };
    vfs.bridge().wait_for_a_held_write();
    let finishing = {
        let vfs = Arc::clone(&vfs);
        thread::spawn(move || vfs.finish())
    };

    // An absence is bought with time: long enough for a finish that does not wait to return.
    thread::sleep(Duration::from_millis(300));
    let finished_early = finishing.is_finished();
    vfs.bridge().let_writes_through();
    releasing.join().unwrap();
    finishing.join().unwrap();

    assert!(!finished_early, "finish returned while the release's commit was still in flight");
    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite new\n")]);
}

// `sed -i` on a file the mount will not let be written: the rename onto it is refused, and the
// temp is still where sed left it — the command was told the rename failed, so nothing moved.
#[test]
fn a_refused_rename_onto_a_path_leaves_the_new_file_where_it_was() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");
    let (temp, fh) = vfs.create(notes, "sedXYZ").unwrap();
    vfs.write(fh, 0, b"new\n").unwrap();
    vfs.release(fh);
    vfs.bridge().refuse(NOTE);

    assert_eq!(vfs.rename(notes, "sedXYZ", notes, "todo.md"), Err(libc::EACCES));

    assert_eq!(vfs.lookup(notes, "sedXYZ").unwrap().ino, temp.ino);
    assert_eq!(read_all(&vfs, "/vault/notes/sedXYZ"), "new\n");
    assert_eq!(read_all(&vfs, NOTE), "old\n");
}

// `exec 3>t; mv t done.md; echo more >&3`: the rename commits the file while it is still open, and
// the descriptor follows it — what it writes afterwards is a write to the path it was renamed to.
#[test]
fn a_new_file_still_open_when_renamed_goes_on_being_written_at_its_new_path() {
    let vfs = vault();
    let notes = ino(&vfs, "/vault/notes");
    let (_, fh) = vfs.create(notes, "t").unwrap();
    vfs.write(fh, 0, b"one\n").unwrap();

    vfs.rename(notes, "t", notes, "done.md").unwrap();
    vfs.write(fh, 4, b"two\n").unwrap();
    vfs.release(fh);
    vfs.finish();

    assert_eq!(
        vfs.bridge().mutations(),
        ["write /vault/notes/done.md create one\n", "write /vault/notes/done.md overwrite one\ntwo\n"]
    );
}

// `tail -f in >> /vault/notes/todo.md &` with its output redirected: the job outlives the command
// and never releases the file. What it had written by the end is committed with everything else
// the command left, and listed — not dropped without a word when the mount goes away.
#[test]
fn what_was_written_to_a_file_still_open_at_the_end_is_committed() {
    let vfs = vault().with_release_grace(Duration::from_millis(20));
    let opened = vfs.open(ino(&vfs, NOTE), true, false).unwrap();
    vfs.write(opened.fh, 4, b"more\n").unwrap();

    vfs.finish();

    assert_eq!(vfs.bridge().mutations(), [format!("write {NOTE} overwrite old\nmore\n")]);
}

// A file opened for writing and never written is not a change, at the end as at a release.
#[test]
fn a_file_open_and_unwritten_at_the_end_commits_nothing() {
    let vfs = vault().with_release_grace(Duration::from_millis(20));
    vfs.open(ino(&vfs, NOTE), true, false).unwrap();

    vfs.finish();

    assert!(vfs.bridge().mutations().is_empty());
}
