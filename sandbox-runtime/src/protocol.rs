//! What the sandbox server and the launcher say to each other over the launcher's socket: one
//! request line per connection, one response line back. Field names are the .NET side's
//! (`Infrastructure/Clients/Bash/LauncherRunner.cs`), camelCase on the wire.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExecRequest {
    pub command: String,
    /// Absolute, already jailed by the server: the launcher never decides containment.
    pub cwd: String,
    /// Already clamped by the server to its own maximum.
    pub timeout_seconds: u64,
    pub output_cap_bytes: usize,
    /// Exactly the command's environment. Nothing of the launcher's own is inherited.
    pub env: BTreeMap<String, String>,
    /// Where present, the call's other mounts are served at /vfs through the agent's bridge.
    #[serde(default)]
    pub bridge: Option<BridgeGrant>,
}

/// The call token, and where the bridge it opens answers. Handed to the call's daemon over its
/// stdin and to nothing the command can see.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BridgeGrant {
    pub url: String,
    pub token: String,
}

#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExecResponse {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
    pub timed_out: bool,
    pub truncated: bool,
    pub duration_ms: u64,
}

/// A request the launcher could not run at all, as opposed to a command that ran and failed.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExecFailure {
    pub error: String,
    /// `not_found` for a working directory missing from the exec's namespace; absent otherwise.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub code: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_request_reads_the_servers_camel_case() {
        let line = r#"{"command":"echo hi","cwd":"/home/sandbox_user","timeoutSeconds":60,"outputCapBytes":65536,"env":{"HOME":"/home/sandbox_user"}}"#;

        let request: ExecRequest = serde_json::from_str(line).unwrap();

        assert_eq!(request.command, "echo hi");
        assert_eq!(request.timeout_seconds, 60);
        assert_eq!(request.env["HOME"], "/home/sandbox_user");
        assert_eq!(request.bridge, None);
    }

    #[test]
    fn a_request_may_carry_the_bridge() {
        let line = r#"{"command":"ls /vault","cwd":"/","timeoutSeconds":5,"outputCapBytes":10,"env":{},"bridge":{"url":"http://agent:8080/api/vfs-bridge","token":"t"}}"#;

        let request: ExecRequest = serde_json::from_str(line).unwrap();

        assert_eq!(
            request.bridge,
            Some(BridgeGrant { url: "http://agent:8080/api/vfs-bridge".into(), token: "t".into() })
        );
    }

    #[test]
    fn a_response_writes_camel_case() {
        let response = ExecResponse {
            stdout: "hi\n".into(),
            stderr: String::new(),
            exit_code: 0,
            timed_out: false,
            truncated: false,
            duration_ms: 3,
        };

        let json = serde_json::to_string(&response).unwrap();

        assert!(json.contains(r#""exitCode":0"#));
        assert!(json.contains(r#""timedOut":false"#));
        assert!(json.contains(r#""durationMs":3"#));
    }
}
