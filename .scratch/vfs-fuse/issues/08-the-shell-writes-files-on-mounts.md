# 08 — The shell writes files on mounts

**What to build:** A command can create and change files on a mount, and the mount decides what each write means, exactly as the matching file tool call would. That holds because the bridge dispatches to the same domain operations the tools use.

- A write to a file the mount reads as text is a create with overwrite.
- Any other write is a blob write, only where the mount offers one.
- An existing file commits on its last close.
- A rename onto an existing path is a write to that path.
- A new file is held in the call's cache until it is renamed onto a path, or the command exits.
- `mkdir` lives in the cache until a file lands under it.

Every outcome is logged against the call token and returned on the exec result as `vfsChanges`, because bash swallows close errors.

**Blocked by:** 07 — The shell reads every mount

**Status:** ready-for-agent

- [ ] `echo text > /vault/new.md` creates the note once, with no empty intermediate commit
- [ ] `sed -i` on a vault note commits one write to the note, and its temp file never appears on the mount
- [ ] `sed -i` on a file the mount refuses leaves it unchanged and lists a refused write with the mount's envelope
- [ ] A write the matching tool would refuse (a disallowed extension, a rendered file that refuses writes) is refused the same way
- [ ] `mkdir -p /vault/a/b && echo x > /vault/a/b/c.md` creates the note with its directories; an empty `mkdir` leaves nothing on the mount
- [ ] The exec result carries `vfsChanges` with virtual paths, operation and status; backends without a bridge return it empty
