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

    fn record(&self, call: String) {
        self.calls.lock().unwrap().push(call);
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

    fn read(&self, path: &str) -> Result<Vec<u8>, Errno> {
        self.record(format!("read {path}"));
        match self.nodes.lock().unwrap().get(path) {
            Some(FakeNode::File(content, _)) => Ok(content.clone()),
            Some(_) => Err(libc::EISDIR),
            None => Err(libc::ENOENT),
        }
    }
}
