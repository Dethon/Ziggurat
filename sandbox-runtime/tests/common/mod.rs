#![allow(dead_code)]

//! A bridge in memory, counting what it is asked, for the daemon core's tests.

use std::collections::BTreeMap;
use std::sync::{Condvar, Mutex};

use sandbox_runtime::vfs::bridge::{ActionOutput, Attr, Bridge, Entry, Errno, Kind, Listing};

#[derive(Clone)]
pub enum FakeNode {
    Dir,
    /// Content, and whether the mount reports its size (a disk root does, a rendered file not).
    File(Vec<u8>, bool),
    Action,
}

#[derive(Default)]
pub struct FakeBridge {
    pub nodes: Mutex<BTreeMap<String, FakeNode>>,
    pub calls: Mutex<Vec<String>>,
    /// Paths whose writes the mount refuses, as a mount refusing them would.
    pub refusing: Mutex<Vec<String>>,
    /// How many entries a listing carries before the mount cuts it short and says so.
    pub listing_cap: Mutex<Option<usize>>,
    pub revoked: Mutex<bool>,
    /// The agent cannot be reached when the revocation is sent: it never learns of it.
    pub revocation_fails: Mutex<bool>,
    /// Writes wait while it holds, so a test can catch a commit mid-flight.
    pub hold: Mutex<Hold>,
    pub hold_changed: Condvar,
}

#[derive(Default)]
pub struct Hold {
    pub holding: bool,
    /// How many writes are waiting on the hold right now.
    pub waiting: usize,
}

impl FakeBridge {
    pub fn with(nodes: &[(&str, FakeNode)]) -> Self {
        let bridge = FakeBridge::default();
        {
            let mut map = bridge.nodes.lock().unwrap();
            map.insert("/".into(), FakeNode::Dir);
            nodes.iter().for_each(|(path, node)| {
                map.insert(path.to_string(), node.clone());
            });
        }
        bridge
    }

    pub fn calls(&self) -> Vec<String> {
        self.calls.lock().unwrap().clone()
    }

    /// Only what changed something: the reads and listings a command makes are its own business.
    pub fn mutations(&self) -> Vec<String> {
        self.calls()
            .into_iter()
            .filter(|c| !(c.starts_with("attr ") || c.starts_with("list ") || c.starts_with("read ")))
            .collect()
    }

    pub fn hold_writes(&self) {
        self.hold.lock().unwrap().holding = true;
    }

    pub fn let_writes_through(&self) {
        self.hold.lock().unwrap().holding = false;
        self.hold_changed.notify_all();
    }

    /// Blocks until a write has reached the bridge and is waiting on the hold.
    pub fn wait_for_a_held_write(&self) {
        let mut hold = self.hold.lock().unwrap();
        while hold.waiting == 0 {
            hold = self.hold_changed.wait(hold).unwrap();
        }
    }

    fn wait_while_held(&self) {
        let mut hold = self.hold.lock().unwrap();
        if !hold.holding {
            return;
        }
        hold.waiting += 1;
        self.hold_changed.notify_all();
        while hold.holding {
            hold = self.hold_changed.wait(hold).unwrap();
        }
        hold.waiting -= 1;
    }

    pub fn cap_listings_at(&self, entries: usize) {
        *self.listing_cap.lock().unwrap() = Some(entries);
    }

    pub fn refuse(&self, path: &str) {
        self.refusing.lock().unwrap().push(path.to_string());
    }

    pub fn content(&self, path: &str) -> Option<String> {
        match self.nodes.lock().unwrap().get(path) {
            Some(FakeNode::File(bytes, _)) => Some(String::from_utf8_lossy(bytes).into_owned()),
            _ => None,
        }
    }

    // After a revocation the agent logs every commit as dropped and applies none.
    fn record(&self, call: String) {
        let call = if *self.revoked.lock().unwrap() && !call.starts_with("revoke") {
            format!("dropped {call}")
        } else {
            call
        };
        self.calls.lock().unwrap().push(call);
    }

    fn is_revoked(&self) -> bool {
        *self.revoked.lock().unwrap()
    }
}

impl Bridge for FakeBridge {
    fn attr(&self, path: &str) -> Result<Attr, Errno> {
        self.record(format!("attr {path}"));
        match self.nodes.lock().unwrap().get(path) {
            Some(FakeNode::Dir) => Ok(Attr { kind: Kind::Dir, size: None }),
            Some(FakeNode::Action) => Ok(Attr { kind: Kind::Action, size: None }),
            Some(FakeNode::File(content, sized)) => Ok(Attr {
                kind: Kind::File,
                size: sized.then_some(content.len() as u64),
            }),
            None => Err(libc::ENOENT),
        }
    }

    fn list(&self, path: &str) -> Result<Listing, Errno> {
        self.record(format!("list {path}"));
        let prefix = if path == "/" { "/".to_string() } else { format!("{path}/") };
        let nodes = self.nodes.lock().unwrap();
        let entries: Vec<Entry> = nodes
            .iter()
            .filter(|(p, _)| p.starts_with(&prefix) && p.len() > prefix.len() && !p[prefix.len()..].contains('/'))
            .map(|(p, node)| Entry {
                name: p[prefix.len()..].to_string(),
                kind: match node {
                    FakeNode::Dir => Kind::Dir,
                    FakeNode::Action => Kind::Action,
                    FakeNode::File(..) => Kind::File,
                },
            })
            .collect();
        match *self.listing_cap.lock().unwrap() {
            Some(cap) if entries.len() > cap => {
                Ok(Listing { entries: entries.into_iter().take(cap).collect(), truncated: true })
            }
            _ => Ok(Listing { entries, truncated: false }),
        }
    }

    fn write(&self, path: &str, content: &[u8], new: bool) -> Result<(), Errno> {
        self.wait_while_held();
        self.record(format!(
            "write {path} {} {}",
            if new { "create" } else { "overwrite" },
            String::from_utf8_lossy(content)
        ));
        if self.is_revoked() || self.refusing.lock().unwrap().iter().any(|p| p == path) {
            return Err(libc::EACCES);
        }
        let mut nodes = self.nodes.lock().unwrap();
        // createDirectories: the parents a held mkdir made come into being with the file.
        let mut parent = path.rsplit_once('/').map(|(p, _)| p.to_string()).unwrap_or_default();
        while !parent.is_empty() && !nodes.contains_key(&parent) {
            nodes.insert(parent.clone(), FakeNode::Dir);
            parent = parent.rsplit_once('/').map(|(p, _)| p.to_string()).unwrap_or_default();
        }
        nodes.insert(path.to_string(), FakeNode::File(content.to_vec(), true));
        Ok(())
    }

    fn delete(&self, path: &str) -> Result<(), Errno> {
        self.record(format!("delete {path}"));
        if self.is_revoked() || self.refusing.lock().unwrap().iter().any(|p| p == path) {
            return Err(libc::EACCES);
        }
        let mut nodes = self.nodes.lock().unwrap();
        nodes.retain(|p, _| p != path && !p.starts_with(&format!("{path}/")));
        Ok(())
    }

    fn rename(&self, from: &str, to: &str, overwrite: bool) -> Result<(), Errno> {
        self.record(format!("rename {from} {to}{}", if overwrite { " overwrite" } else { "" }));
        if self.is_revoked() || self.refusing.lock().unwrap().iter().any(|p| p == from || p == to) {
            return Err(libc::EACCES);
        }
        let mut nodes = self.nodes.lock().unwrap();
        let moved: Vec<(String, FakeNode)> = nodes
            .iter()
            .filter(|(p, _)| *p == from || p.starts_with(&format!("{from}/")))
            .map(|(p, n)| (p.clone(), n.clone()))
            .collect();
        moved.into_iter().for_each(|(p, node)| {
            nodes.remove(&p);
            nodes.insert(format!("{to}{}", &p[from.len()..]), node);
        });
        // The mount's move creates the destination's parents, as a write does: a held mkdir's
        // directory comes into being with the first thing moved into it.
        let mut parent = to.rsplit_once('/').map(|(p, _)| p.to_string()).unwrap_or_default();
        while !parent.is_empty() && !nodes.contains_key(&parent) {
            nodes.insert(parent.clone(), FakeNode::Dir);
            parent = parent.rsplit_once('/').map(|(p, _)| p.to_string()).unwrap_or_default();
        }
        Ok(())
    }

    // Answers with the files beside the action, so a test can see what the action saw.
    fn action(&self, path: &str, argv: &[String]) -> Result<ActionOutput, Errno> {
        self.record(format!("action {path} {}", argv.join(" ")));
        let dir = path.rsplit_once('/').map(|(d, _)| d.to_string()).unwrap_or_default();
        let beside: Vec<String> = self
            .nodes
            .lock()
            .unwrap()
            .keys()
            .filter(|p| p.starts_with(&format!("{dir}/")) && !p[dir.len() + 1..].contains('/'))
            .map(|p| p[dir.len() + 1..].to_string())
            .collect();
        Ok(ActionOutput { stdout: beside.join("\n"), stderr: String::new(), exit_code: 0 })
    }

    fn revoke(&self) -> Result<(), Errno> {
        self.record("revoke".into());
        if *self.revocation_fails.lock().unwrap() {
            return Err(libc::EIO);
        }
        *self.revoked.lock().unwrap() = true;
        Ok(())
    }

    fn read(&self, path: &str) -> Result<Vec<u8>, Errno> {
        self.record(format!("read {path}"));
        match self.nodes.lock().unwrap().get(path) {
            Some(FakeNode::File(content, _)) => Ok(content.clone()),
            Some(_) => Err(libc::EISDIR),
            None => Err(libc::ENOENT),
        }
    }
}
