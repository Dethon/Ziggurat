# 0025 — A mount declares where it can be written

Status: accepted
Date: 2026-08-13

## Context

ADR-0020 puts an attachment's bytes into the sandbox as a real file, and ADR-0021 names the
place: `~/uploads/<conversation>/<message-id>/<filename>`. The code that landed drifted from
that. `AttachmentLanding` builds its target from the mount point — `{mountPoint}/uploads/...` —
and the sandbox mount *is* the container root, so every attachment targeted `/uploads`. The
image never creates that directory and the container runs unprivileged, so the write failed,
`TryWriteAsync` logged a warning, and the turn continued with nothing landed and nothing said.

Confirmed on the running stack: `mcp-sandbox` runs as uid 1000, `/` is `drwxr-xr-x root root`,
`/uploads` does not exist. No attachment has ever landed. The only writable persistent path in
that container is `/home/sandbox_user`, the compose bind mount.

Restoring ADR-0021's path means the landing code has to know where the writable directory is.
It deliberately does not know: it picks its target mount by asking for the exec capability
rather than by name, so it does not depend on one server's spelling. Two other places could
hold the knowledge instead. Domain could carry the literal `home/sandbox_user`, which puts one
image's layout into the layer that was built not to have it. Or the whole target could become
an agent-side setting, which can disagree with the compose volume and breaks silently when the
filesystem is renamed, since it bypasses the capability lookup that finds the mount.

## Decision

**A mount publishes its workspace — the writable, persistent directory under it — and the
landing code asks the mount rather than knowing the answer.**

The backend declares it, the `filesystem://` resource carries it beside `name`, `mountPoint`
and `description`, and discovery puts it on `FileSystemMount`. It is published in the backend's
own coordinates, and Domain composes the virtual path through `FileSystemResolution.ToVirtualPath`
— the one implementation of that translation — so the backend asserts its own mount point once,
in one field, and ADR-0016's rule holds unchanged.

The field is nullable and most mounts leave it null. **An exec-capable mount that declares no
workspace lands nothing**, and the model is told so. Falling back to the mount root would
preserve exactly the silent failure this ADR exists to remove.

## Consequences

- The sandbox's `HomeDir` setting stays. The sandbox path-consistency work deletes it from the
  command runner's options on the grounds that a setting feeding only prose is worse than
  prose; that argument stops applying here, because the value now drives where a file goes.
  Its criterion is narrowed rather than performed and reversed.
- The sandbox prompt is built from the mount point and the workspace instead of asserting both
  as literals eight times over, so a rename cannot leave the prose behind.
- `VfsExecTool`'s description stops naming `/sandbox/home/sandbox_user`. It is a `const` in a
  `[Description]` attribute and cannot interpolate, and a mount-agnostic tool naming one mount's
  layout is what produced the duplicate. The sandbox facts live in the sandbox prompt.
- Nothing sweeps a landed attachment. It sits in the workspace like any file the agent wrote
  there, visible to the person who owns the volume and removable by them. A second retention
  clock was rejected: a landed attachment is no more transient than a script the agent left
  behind, and nothing sweeps that either.
- A future mount that wants a workspace declares one. Nothing else has to change, and nothing
  reads the field except the code that decides where an attachment goes.
