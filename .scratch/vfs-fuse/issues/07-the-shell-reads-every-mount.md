# 07 — The shell reads every mount

**What to build:** The tracer bullet. From a sandbox exec, `cat`, `grep`, `ls` and `jq` work on every non-outpost mount the calling session has, at its virtual path: `/vault`, `/timers`, `/schedules`, `/ha`. Rendered files are read in full. It runs end to end:

1. The exec tool mints a call token bound to the calling session and passes it on the call's metadata.
2. The launcher starts one FUSE daemon per exec holding that token, at `/vfs`, with a link per mount.
3. The daemon answers attributes, listings and reads through a new bridge endpoint on the agent.
4. The bridge resolves them through the session's registry.
5. The token is revoked when exec returns.

Writes are not served yet: they fail read-only. The sandbox service gains the FUSE device and `SYS_ADMIN` capability.

**Blocked by:** 06 — Sandbox commands run through a root launcher

**Status:** ready-for-agent

- [ ] `grep -r <word> /vault` from a sandbox exec finds what the text search tool finds
- [ ] `jq . /timers/<id>/status.json` (or the current rendered file) reads the full rendered content; a file whose size the mount does not report is served with direct I/O and never reads as empty
- [ ] `ls` of a rendered directory lists what glob lists
- [ ] Outposts are not under `/vfs`, and the sandbox's own disk is not served through FUSE
- [ ] A mount whose identity collides with a non-empty image directory, or `sandbox`, is served only at `/vfs/<name>`; the empty `/media` is removed at image build
- [ ] The bridge refuses an unknown, revoked or expired token, and is not routed by the public proxy
- [ ] The token never appears in the command's environment or any file it can read
- [ ] Concurrent execs from two sessions each see only their own session's mounts
- [ ] Tested at the agreed seams: the exec tool with a scripted fake sandbox, the daemon core with a fake bridge, and the real image with a stub bridge
