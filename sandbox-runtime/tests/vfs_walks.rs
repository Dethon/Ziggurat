//! A recursive command over a big mount: what it costs in bridge requests, and what the call's
//! cache keeps and drops.

mod common;

use common::{FakeBridge, FakeNode};
use sandbox_runtime::vfs::bridge::Kind;
use sandbox_runtime::vfs::core::{Vfs, ROOT};

fn tree(dirs: usize, files_per_dir: usize) -> Vfs<FakeBridge> {
    let mut nodes = vec![("/vault".to_string(), FakeNode::Dir)];
    (0..dirs).for_each(|d| {
        nodes.push((format!("/vault/d{d}"), FakeNode::Dir));
        (0..files_per_dir).for_each(|f| {
            nodes.push((format!("/vault/d{d}/n{f}.md"), FakeNode::File(format!("note {d}/{f} TODO\n").into_bytes(), true)));
        });
    });
    let borrowed: Vec<(&str, FakeNode)> = nodes.iter().map(|(p, n)| (p.as_str(), n.clone())).collect();
    Vfs::new(FakeBridge::with(&borrowed))
}

// What `grep -r` does to the kernel: list each directory, look up and stat every entry — more than
// once, as the kernel does with zero TTLs — and read every file.
fn grep_r(vfs: &Vfs<FakeBridge>, ino: u64) -> usize {
    vfs.readdir(ino)
        .unwrap()
        .into_iter()
        .map(|entry| {
            let node = vfs.lookup(ino, &entry.name).unwrap();
            vfs.getattr(node.ino).unwrap();
            vfs.getattr(node.ino).unwrap();
            match node.kind {
                Kind::Dir => grep_r(vfs, node.ino),
                _ => {
                    let opened = vfs.open(node.ino, false, false).unwrap();
                    let hit = vfs.read(opened.fh, 0, 1 << 16).unwrap().windows(4).any(|w| w == b"TODO");
                    vfs.release(opened.fh);
                    usize::from(hit)
                }
            }
        })
        .sum()
}

#[test]
fn a_recursive_grep_costs_one_listing_per_directory_and_one_attr_per_file() {
    let vfs = tree(20, 50);
    let vault = vfs.lookup(ROOT, "vault").unwrap().ino;
    let before = vfs.bridge().calls().len();

    let hits = grep_r(&vfs, vault);

    let calls: Vec<String> = vfs.bridge().calls()[before..].to_vec();
    let count = |op: &str| calls.iter().filter(|c| c.starts_with(op)).count();
    assert_eq!(hits, 1000);
    assert_eq!(count("list "), 21, "one listing per directory");
    assert!(count("attr ") <= 1000, "at most one attr fetch per file, got {}", count("attr "));
    assert_eq!(count("read "), 1000);
}

// Asked twice in one command, a file is fetched once.
#[test]
fn a_file_read_twice_is_fetched_once() {
    let vfs = tree(1, 1);
    let dir = vfs.lookup(vfs.lookup(ROOT, "vault").unwrap().ino, "d0").unwrap().ino;
    let note = vfs.lookup(dir, "n0.md").unwrap().ino;

    (0..2).for_each(|_| {
        let opened = vfs.open(note, false, false).unwrap();
        vfs.release(opened.fh);
    });

    assert_eq!(vfs.bridge().calls().iter().filter(|c| c.starts_with("read ")).count(), 1);
}

// The root is the daemon's own: a directory whatever the mounts say, so the kernel's first stat of
// the mount costs no round trip — out through the home's tunnel and back, on prod.
#[test]
fn the_root_is_a_directory_without_asking() {
    let vfs = Vfs::new(FakeBridge::with(&[("/vault", FakeNode::Dir)]));

    assert_eq!(vfs.getattr(ROOT).unwrap().kind, Kind::Dir);
    assert!(vfs.bridge().calls().is_empty(), "asked {:?}", vfs.bridge().calls());
}

// The agent names the served mounts with the call, so the root is never listed: not at start, where
// it held up the command by a round trip, and not after a write dropped the call's cache.
#[test]
fn the_served_names_given_at_start_answer_the_root_for_the_whole_call() {
    let vfs = Vfs::new(FakeBridge::with(&[
        ("/vault", FakeNode::Dir),
        ("/vault/a.md", FakeNode::File(b"a".to_vec(), true)),
        ("/timers", FakeNode::Dir),
    ]))
    .with_served(vec!["timers".into(), "vault".into()]);

    assert_eq!(vfs.served().unwrap(), ["timers", "vault"]);
    let vault = vfs.lookup(ROOT, "vault").unwrap();
    assert_eq!(vault.kind, Kind::Dir);
    assert_eq!(vfs.lookup(ROOT, "nope"), Err(libc::ENOENT));
    let (_, fh) = vfs.create(vault.ino, "b.md").unwrap();
    vfs.release(fh);
    vfs.finish();
    let names: Vec<String> = vfs.readdir(ROOT).unwrap().into_iter().map(|e| e.name).collect();
    vfs.lookup(ROOT, "vault").unwrap();

    assert_eq!(names, ["timers", "vault"]);
    let asked: Vec<String> = vfs
        .bridge()
        .calls()
        .into_iter()
        .filter(|c| c == "list /" || c.strip_prefix("attr /").is_some_and(|rest| !rest.contains('/')))
        .collect();
    assert!(asked.is_empty(), "asked {asked:?}");
}

// The working directory is asked for once, before the command runs: every directory on the way, its
// listing, and so what an action file there is. Reaching it and running `./turn_on` in it then
// costs the kernel nothing more — each was a round trip out through the home's tunnel, one after
// another, before the action itself.
#[test]
fn a_working_directory_walked_at_start_costs_nothing_more_to_reach_and_run_an_action_in() {
    let vfs = Vfs::new(FakeBridge::with(&[
        ("/ha", FakeNode::Dir),
        ("/ha/entities", FakeNode::Dir),
        ("/ha/entities/climate", FakeNode::Dir),
        ("/ha/entities/climate/office", FakeNode::Dir),
        ("/ha/entities/climate/office/turn_on", FakeNode::Action),
        ("/ha/entities/climate/office/state.json", FakeNode::File(b"{}".to_vec(), false)),
    ]))
    .with_served(vec!["ha".into()]);

    vfs.prefetch("/ha/entities/climate/office");
    let office = ["ha", "entities", "climate", "office"].iter().fold(ROOT, |ino, name| {
        let node = vfs.lookup(ino, name).unwrap();
        vfs.getattr(node.ino).unwrap();
        node.ino
    });
    let action = vfs.lookup(office, "turn_on").unwrap();
    vfs.getattr(action.ino).unwrap();
    assert_eq!(vfs.lookup(office, "missing"), Err(libc::ENOENT));

    assert_eq!(action.kind, Kind::Action);
    assert_eq!(vfs.bridge().calls(), ["walk /ha/entities/climate/office"]);
}

// A walk that could not be had changes nothing: the kernel asks step by step, as it always did.
#[test]
fn a_walk_into_nothing_leaves_the_kernel_to_ask() {
    let vfs = Vfs::new(FakeBridge::with(&[("/ha", FakeNode::Dir)])).with_served(vec!["ha".into()]);

    vfs.prefetch("/ha/nope");

    assert_eq!(vfs.lookup(ROOT, "ha").map(|n| n.kind), Ok(Kind::Dir));
    let ha = vfs.lookup(ROOT, "ha").unwrap().ino;
    assert_eq!(vfs.lookup(ha, "nope"), Err(libc::ENOENT));
    assert_eq!(vfs.bridge().calls(), ["walk /ha/nope", "attr /ha/nope"]);
}
