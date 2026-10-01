# 09 — The shell deletes and moves

**What to build:** `rm` and `rmdir` are the mount's delete, so `rm -r /timers/<id>` cancels the timer as the remove tool does. `mv` inside one mount is that mount's move. `mv` between two mounts arrives as one rename, because both sit in one FUSE filesystem behind links. The bridge turns it into a transfer with move intent, so the move-out check is asked before anything is copied. Every outcome lands in `vfsChanges`.

**Blocked by:** 08 — The shell writes files on mounts

**Status:** ready-for-agent

- [x] `rm -r` of a timer through the shell cancels it, exactly as the remove tool does
- [x] `mv` within the vault is reported as a move, not a delete and a create
- [x] `mv` between two mounts is a transfer; a move-out refusal keeps the source and is listed as refused
- [x] `mv` between the sandbox's home and a mount stays a copy and an unlink by the kernel, and the mount sees a create or a delete
- [x] Each delete and move appears in `vfsChanges`
