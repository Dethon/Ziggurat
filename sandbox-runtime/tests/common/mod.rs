#![allow(dead_code)]

//! A bridge in memory, counting what it is asked, for the daemon core's tests.

use std::collections::BTreeMap;
use std::sync::Mutex;

use sandbox_runtime::vfs::bridge::{Attr, Bridge, Entry, Errno, Kind, Listing};

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
    pub revoked: Mutex<bool>,
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
        let entries = nodes
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
        Ok(Listing { entries, truncated: false })
    }

    fn write(&self, path: &str, content: &[u8], new: bool) -> Result<(), Errno> {
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

    fn delete(&self, path: &str, directory: bool) -> Result<(), Errno> {
        self.record(format!("delete {path}{}", if directory { "/" } else { "" }));
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
        Ok(())
    }

    fn revoke(&self) -> Result<(), Errno> {
        self.record("revoke".into());
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
