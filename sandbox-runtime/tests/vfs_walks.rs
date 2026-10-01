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
