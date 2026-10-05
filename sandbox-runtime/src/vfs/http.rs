//! The bridge over HTTP: one POST per operation to the agent, with the call token as a bearer.
//! 200 is a value (JSON, or a read's raw bytes); 422 is a refusal naming its errno; anything
//! else — 401 for a token the agent no longer knows, a network failure — is EIO or EACCES.

use std::time::Duration;

use serde_json::Value;

use super::bridge::{errno_named, ActionOutput, Attr, Bridge, Entry, Errno, Kind, Listing, MAX_FILE};

pub struct HttpBridge {
    agent: ureq::Agent,
    url: String,
    authorization: String,
}

/// A revocation is one small request ahead of a kill. It gets a bound of its own, well inside the
/// unit's wait for it, so an agent that is slow to answer is one the daemon seals itself against
/// rather than one the unit gives up on.
pub const REVOKE_WITHIN: Duration = Duration::from_secs(10);

impl HttpBridge {
    pub fn new(url: &str, token: &str) -> Self {
        let agent: ureq::Agent = ureq::Agent::config_builder()
            .http_status_as_error(false)
            .timeout_global(Some(Duration::from_secs(120)))
            .build()
            .into();
        Self {
            agent,
            url: url.trim_end_matches('/').to_string(),
            authorization: format!("Bearer {token}"),
        }
    }

    fn post(&self, op: &str, query: &[(&str, &str)], body: &[u8]) -> Result<Vec<u8>, Errno> {
        self.post_with_type(op, query, body, "application/octet-stream")
    }

    fn post_with_type(&self, op: &str, query: &[(&str, &str)], body: &[u8], content_type: &str) -> Result<Vec<u8>, Errno> {
        self.send(op, query, body, content_type, None)
    }

    fn send(
        &self,
        op: &str,
        query: &[(&str, &str)],
        body: &[u8],
        content_type: &str,
        within: Option<Duration>,
    ) -> Result<Vec<u8>, Errno> {
        let request = query
            .iter()
            .fold(self.agent.post(format!("{}/{op}", self.url)), |r, (k, v)| r.query(*k, *v))
            .header("Authorization", &self.authorization)
            .header("Content-Type", content_type);
        let request = match within {
            Some(within) => request.config().timeout_global(Some(within)).build(),
            None => request,
        };
        let mut response = request.send(body).map_err(|_| libc::EIO)?;
        let status = response.status().as_u16();
        let bytes = response.body_mut().with_config().limit(MAX_FILE).read_to_vec().map_err(|_| libc::EIO)?;
        match status {
            200 => Ok(bytes),
            422 => Err(serde_json::from_slice::<Value>(&bytes)
                .ok()
                .and_then(|v| v["errno"].as_str().map(errno_named))
                .unwrap_or(libc::EIO)),
            401 => Err(libc::EACCES),
            _ => Err(libc::EIO),
        }
    }

    fn post_json(&self, op: &str, path: &str) -> Result<Value, Errno> {
        let bytes = self.post(op, &[("path", path)], &[])?;
        serde_json::from_slice(&bytes).map_err(|_| libc::EIO)
    }
}

impl Bridge for HttpBridge {
    fn attr(&self, path: &str) -> Result<Attr, Errno> {
        let value = self.post_json("attr", path)?;
        Ok(Attr {
            kind: Kind::parse(value["kind"].as_str().unwrap_or("file")),
            size: value["size"].as_u64(),
        })
    }

    fn list(&self, path: &str) -> Result<Listing, Errno> {
        let value = self.post_json("list", path)?;
        let entries = value["entries"]
            .as_array()
            .map(|entries| {
                entries
                    .iter()
                    .filter_map(|e| {
                        Some(Entry {
                            name: e["name"].as_str()?.to_string(),
                            kind: Kind::parse(e["kind"].as_str().unwrap_or("file")),
                        })
                    })
                    .collect()
            })
            .unwrap_or_default();
        Ok(Listing { entries, truncated: value["truncated"].as_bool().unwrap_or(false) })
    }

    fn read(&self, path: &str) -> Result<Vec<u8>, Errno> {
        self.post("read", &[("path", path)], &[])
    }

    fn delete(&self, path: &str) -> Result<(), Errno> {
        self.post("delete", &[("path", path)], &[])
            .map(|_| ())
    }

    fn rename(&self, from: &str, to: &str, overwrite: bool) -> Result<(), Errno> {
        self.post(
            "rename",
            &[("path", from), ("to", to), ("overwrite", if overwrite { "true" } else { "false" })],
            &[],
        )
        .map(|_| ())
    }

    fn action(&self, path: &str, argv: &[String]) -> Result<ActionOutput, Errno> {
        let body = serde_json::to_vec(&serde_json::json!({ "argv": argv })).map_err(|_| libc::EIO)?;
        let bytes = self.post_with_type("action", &[("path", path)], &body, "application/json")?;
        serde_json::from_slice(&bytes).map_err(|_| libc::EIO)
    }

    fn revoke(&self) -> Result<(), Errno> {
        self.send("revoke", &[], &[], "application/octet-stream", Some(REVOKE_WITHIN)).map(|_| ())
    }

    fn write(&self, path: &str, content: &[u8], new: bool) -> Result<(), Errno> {
        self.post("write", &[("path", path), ("new", if new { "true" } else { "false" })], content)
            .map(|_| ())
    }
}
