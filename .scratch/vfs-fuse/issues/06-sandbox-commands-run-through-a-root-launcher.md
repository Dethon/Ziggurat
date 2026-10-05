# 06 — Sandbox commands run through a root launcher

**What to build:** The sandbox container's entrypoint becomes a small root launcher: the first piece of the third standalone Rust crate. It starts the sandbox MCP server as its own fixed uid. For each exec it runs the command in a private mount namespace as PUID, the owner of the home volume, with the bounding set dropped and the minimal environment from 01. A command can no longer read, trace or kill the server, and cannot gain capabilities through a setuid binary. In-process hosts used by the integration suite keep a direct runner with the same minimal environment, so they run without root.

**Blocked by:** 01 — Commands see no deployment secrets

**Status:** ready-for-agent

- [x] A command runs as PUID with no effective capabilities, with supplementary groups kept
- [x] A command cannot read the server's process environment, `unshare`, or mount a FUSE filesystem (setuid stripped from `fusermount3`)
- [x] Timeout, kill-tree, output caps and exit codes behave exactly as before, over the same exec result
- [x] The integration suite's in-process sandbox still runs, without root
- [x] The crate follows the root rules: no workspace, the same pinned toolchain, in both editors' rust-analyzer project lists; its `cargo test` passes with no .NET built
- [x] Compose, the E2E stack and the eval's sandbox testcontainer start the new image
