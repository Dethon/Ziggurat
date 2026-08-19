# 08 — Sandbox as a testcontainer

Status: resolved

The exec claims are unwitnessed because the eval will not run model-authored shell on the host.
Run the real sandbox image in a disposable container (the E2E stack already builds and drives
it) and dial it from the eval stack like any other mount. Unlocks
`mounts.exec-work-goes-where-exec-lives`, the positive half of
`mounts.capabilities-are-advertised`, and `mounts.transfer-is-one-call` (the sandbox is the
second writable mount). The heaviest issue; last.

## Answer

Done. `EvalSandbox` runs the real mcp-sandbox image per stack — the E2E image helpers build it
once per machine, the container starts in seconds with a throwaway host-mounted workspace, and
`EvalSandboxTests` proves the endpoint answers MCP and advertises fs_exec before any model pays
for a run. Every scenario now runs with the sandbox mounted, which is the shipped toolset — the
same faithfulness argument that added the websearch server.

The consumer is the checksum scenario: sha256 of a vault note, whose expected value the harness
computes from the same bytes the seed writes. It requires the single copy into /sandbox and the
exec there, permits no create (the two-call transfer is the named failure), and cites
`mounts.exec-work-goes-where-exec-lives` and `mounts.transfer-is-one-call`.
`mounts.capabilities-are-advertised` and the vault's transfer sentence flip to guards.
