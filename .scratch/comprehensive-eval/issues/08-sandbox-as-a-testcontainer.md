# 08 — Sandbox as a testcontainer

Status: ready-for-agent

The exec claims are unwitnessed because the eval will not run model-authored shell on the host.
Run the real sandbox image in a disposable container (the E2E stack already builds and drives
it) and dial it from the eval stack like any other mount. Unlocks
`mounts.exec-work-goes-where-exec-lives`, the positive half of
`mounts.capabilities-are-advertised`, and `mounts.transfer-is-one-call` (the sandbox is the
second writable mount). The heaviest issue; last.
