# 03 — The working directory comes back reusable

**What to build:** The directory an exec reports can be passed straight into the next
filesystem call, like every other path in every other response. Today it comes back in the
container's own coordinates and is rejected by the registry, so the obvious next move after
running a command costs a failed call first.

**Blocked by:** 02 — the container root is unreachable as a working directory until the
home-directory default is gone, so the root case cannot be exercised before then.

**Status:** ready-for-agent

- [x] The working directory in an exec response starts with the mount point.
- [x] Passing that value back as a path argument reaches the directory the command actually ran
      in.
- [x] A command run at the container root reports the mount point with a trailing slash, which
      is how glob already spells it and what a trailing slash already means.
- [x] The command runner reports the working directory relative to its own configured root, and
      the exec tool applies the mount point using the shared translation already used for glob
      entries and search hits — neither side guesses at the other's frame.
- [x] Correctness does not depend on the sandbox root happening to be the container root: a
      differently configured root still produces a usable path.
- [x] The virtual-path conformance theory asserts the working directory alongside every other
      path, against all three hostile backend spellings, with no exemption for it.
- [x] ADR-0016 records one exempt field rather than two, and why this one turned out to be
      claimable after all — including why a single field was normalised at the backend when
      normalising backends was rejected as a blanket policy.
- [x] The exec result type documents that the working directory is relative to the backend's own
      root, where someone writing a new backend will read it.
