# 0016 — A tool answers in the coordinates it was asked in

Status: accepted
Date: 2026-08-06

## Context

The agent talks to every filesystem in virtual paths: a mount point followed by a path
under it. That is the only spelling the filesystem prompt teaches, and the only one
`VirtualFileSystemRegistry.Resolve` accepts.

Backends answer in their own coordinates, and they do not agree with each other. A disk
root reports the container-absolute path. Some virtual filesystems report a leading-slash
mount-relative path, others a bare one. That is fine on the wire — the mount point is not
the backend's business — and it is a defect the moment a tool passes it through.

Several tools did. `remove` reported the path it deleted in the backend's spelling, plus a
trash location that sits outside every mount. `create` and `edit` reported the file they
wrote in whichever spelling their backend used, and the two disagreed on the same mount.
The directory branch of a transfer reported per-entry sources without their mount point.

So the model's obvious next move failed. It removed a file, read back the path the response
gave it, fed that to `text_read`, and got "No filesystem mounted for path". Nothing in the
response said the path was not reusable.

The same defect had already been fixed twice, months apart, on whichever tools were noticed
at the time: once for `info` and `read`, once for `text_search`. Each fix added another
private normalizer to another tool. There were four of them, no shared implementation, and
nothing that failed when the next tool forgot.

## Decision

**A tool answers in the coordinates it was asked in. Every path in a response is either the
caller's own string echoed back, or a backend entry with its mount point prefixed. The
backend's own spelling never reaches the model.**

The two halves are different operations, and only one of them is a translation.

Where the caller named the path, the tool **echoes the caller's own argument** — `read`,
`info`, `create`, `edit`, and the original path in `remove`. Translation cannot be used
here: at least one backend answers with the container-absolute path, and prefixing a mount
point onto that produces nonsense.

Where the backend produced paths the caller never named, the tool **translates** through
one shared implementation, `FileSystemResolution.ToVirtualPath`. This covers glob entries,
search hit files, and the per-entry sources and destinations of a directory transfer. The
leading slash a backend may or may not have supplied is trimmed; a trailing one marks a
directory and is preserved.

Backends keep their existing conventions. Normalising every backend to answer
mount-relative was considered and rejected as a wider blast radius for no additional
guarantee — the invariant holds at the tool boundary either way.

What makes forgetting impossible is a test, not a type.
`Tests/Unit/Domain/Tools/FileSystem/VfsVirtualPathConformanceTests.cs` drives every
filesystem tool against a backend answering in three deliberately hostile spellings and
fails if any path in any response is not a virtual path. Its tool set is derived from
`FileSystemOperations.All`, so an operation added without a case is impossible.

One field has no virtual path and is named as an exemption where the rule is enforced: a
glob entry outside the requested source directory of a transfer, which is outside the
coordinate frame the request was made in. The trash location a disk root reports sits
outside every mount; three backends already answered empty and the disk root joins them,
since nothing reads it.

The working directory an `exec` reports was the second exemption and is no longer. It was
exempt because a backend answered it in its own coordinates and four backends meant four
different things by it, so no mount could claim it. That was a fact about the result
contract rather than about the field: the working directory is a path the caller never
named, which is exactly the half of the rule that translates. So the contract says what the
field means — the directory the command ran in, relative to the backend's own configured
root, the root itself being the empty string — and the tool prefixes the mount point through
the same `ToVirtualPath` glob entries and search hits use. The root round-trips as the mount
point with a trailing slash, which is how glob already spells a directory.

Normalising this one field at the backend does not reopen "normalise every backend to
answer mount-relative", rejected below. That was a blanket policy: every operation of every
backend, moving the wire format for a guarantee the tool boundary already gives. This is one
field of one result type, and it buys something the boundary cannot give on its own — the
mount point can only be prefixed onto a path already expressed relative to the backend's
root, and the backend is the only component that knows that root. Where a backend already
answers in coordinates a mount cannot claim, the tool still echoes the caller's own string.

## A tool's result type is not its backend's

This is why `copy` and `move` answer with `FsTransferResult` rather than with the backend's
`FsCopyResult` or `FsMoveResult`. Those are the wire contract for a backend's native
primitive: one mount, one call, paths in that backend's coordinates. The tool-level
operation spans two mounts, recurses directories, and reports per-entry outcomes that no
backend result can express — in virtual paths, because that is the frame it was asked in.

The same reasoning applies to any future tool whose answer is not simply its backend's
answer relabelled. The frames differ, so the types differ.

## Considered options

**A translating decorator over the backend.** Wrap every backend at mount time and rewrite
paths on the way out. Rejected: it needs the same per-result-type table of which fields are
paths, only placed where no tool can see it, and it interposes on every call for a
formatting concern. It also cannot express the echo half — a decorator does not know what
string the caller passed.

**Normalise every backend to answer mount-relative.** Rejected as a wider blast radius for
no additional guarantee. Every MCP server would need touching, the wire format would move,
and the tool boundary would still be the place the invariant has to hold.

**Rewrite backend error prose too.** Out of scope. A jail refusal names the resolved path
and the mount root in the backend's own coordinates, and that string crosses the MCP wire
with no mount context on the far side; fixing it means threading the mount point into every
backend. Only messages a tool builds itself are covered, because those are constructed in
the agent process with the resolution in hand.

## Consequences

- Any path the model reads out of a filesystem response can be fed straight back into
  another filesystem tool.
- Mount-point translation has one implementation, so a change to the convention is one edit
  rather than four.
- A tool that starts answering in backend coordinates fails a test rather than reaching
  production.
- An exemption is a decision spent in the test file with a line of reason, rather than an
  oversight nobody notices — and one that turns out to be claimable is spent back.
- The backend result contract is unchanged but for the one field named above, whose meaning
  is now documented on `FsExecResult` where someone writing a new exec-capable backend
  reads it.
