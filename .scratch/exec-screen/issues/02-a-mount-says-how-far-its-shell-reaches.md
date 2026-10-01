# 02 — A mount says how far its shell reaches

Status: resolved

Spec: `../spec.md` (Decisions: Where a command runs is a mount claim).

Add `ShellReach` (`Contained` | `Host` | null) to `FileSystemMount`, declared by the backend
(`FileSystemBackendBase`, null by default). Publish it in the `filesystem://` resource and read it
at discovery, through the same route as `Workspace` and `IsLandingTarget`.

`SandboxFileSystem` declares `Contained`, `ExecutingOutpostFileSystem` declares `Host`, and every
other backend stays null (`HaFileSystem` included).

Tests:

- discovery carries each value, and an absent one reads null;
- a server's conformance test pins its reach;
- a non-executing `OutpostFileSystem` is null.

Update `.claude/rules/virtual-filesystem.md` with the claim and why it is separate from exec
capability: `/ha` executes and has no shell.
