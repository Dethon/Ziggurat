# 10 — Writes a person would be asked about are refused, and a killed command commits nothing

**What to build:** Two guarantees on the shell route.

1. **Approval parity.** Every bridge operation asks the question the equivalent file tool call would ask, against the agent's whitelist and remembered approvals. One that would prompt a person is refused with `EACCES` and listed, rather than waiting on anyone.
2. **Kill semantics.** When a command times out or the call is cancelled, the token is revoked at the kill. Commits arriving afterwards, held new files included, are dropped and listed as dropped. Operations already in flight finish and are listed.

FUSE operations are not screened; this ticket changes nothing in the exec screen.

**Blocked by:** 08 — The shell writes files on mounts

**Status:** ready-for-agent

- [ ] For an agent whose whitelist covers the file tools, shell writes are applied
- [ ] For an agent whose whitelist does not, a shell write is refused with `EACCES`, listed, and no prompt is raised
- [ ] A remembered approval for the equivalent tool lets the shell write through
- [ ] A command killed by its timeout mid-write leaves the mount unchanged, and the write is listed as dropped
- [ ] A cancelled call behaves like a timeout
