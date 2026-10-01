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
