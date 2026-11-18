# 02: A mount point may be a union of filesystems

**What to build:** A filesystem resource may declare `union` (`routeBy` JSON pointer with `*` for array items, plus `claims` or `fallback: true`). Discovery reads it onto `FileSystemMount`. The registry merges members declaring `union` at one mount point into a generic `UnionFileSystem` (Domain); a non-union collision keeps first-wins. The union binds every member to the caller, merges glob/search, sends an existing top-level entry's operations to its holder, routes a new entry's create by claims (fallback when nothing is claimed; refusal naming values when two members' claims mix), reports failed members in a defaulted `unavailable` field of the glob result, and drops a member that refuses the caller. Capabilities = union of members' operations. No watch-specific code. Spec: "Union mounts".

**Blocked by:** 01

Status: done

- [x] Two in-memory members at one mount: listing merges, read/edit/remove reach the holder, a create routes by claim, an unclaimed create goes to the fallback, a mixed create is refused naming the values.
- [x] A create of an entry another member holds goes to the holder.
- [x] A member answering `permission_denied` adds nothing and fails nothing; a member that throws a transient error is named in `unavailable`.
- [x] Caller binding reaches every member (a member sees the call's conversation).
- [x] A union member declaring a workspace, shell reach or landing target is not mounted as a member (logged).
- [x] `VfsVirtualPathConformanceTests` cover a union; the bridge serves a union mount's files to a sandbox command (`VfsBridgeOverTheWireTests`).

## Comments

Done in 09867e5bd. Deviations and things
worth knowing:

- **Where the code is.** `FileSystemUnion` (`Domain/DTOs/`) is the claim; `FileSystemBackendBase.Union`
  (virtual, null) declares it; `FileSystemServerResource.Describe` publishes it as
  `"union":{"routeBy":…,"claims":[…],"fallback":…}` (and `"union":null` on every other mount — the one
  byte change every resource body makes); discovery reads it onto `FileSystemMount.Union` (no
  `routeBy` reads as no union). `UnionFileSystem` (`Domain/Tools/FileSystem/`) is the composite and
  owns the join rule (`UnionFileSystem.Join`, `RefusalAsMember`); `VirtualFileSystemRegistry.TryMount`
  gained an overload with `out string? refusal`, which discovery logs as a warning and lists with the
  shadowed. A plain collision is still first-wins with the old log line.
- **Decided and pinned (tests in `VirtualFileSystemRegistryTests` / `UnionFileSystemTests`):**
  - A union-declaring mount with a workspace, a shell reach (or occupied names) or a landing claim is
    refused **whether it arrives first or second**, so the outcome does not hang on dial order.
  - Members must share one `routeBy`; a second `fallback` and a value already claimed are refused at
    join, so a value is never two members'.
  - A non-string value at the pointer is spelled as JSON and so claimed by nobody (fallback's).
  - No fallback: a create with no claimed value (or nothing at the pointer) is refused
    `invalid_argument`, naming what each member claims in the hint.
  - A move or copy within the mount between two members' entries is refused `invalid_argument`
    ("read it and write it anew"); a destination nobody holds is the source holder's.
  - A create while a member is away is still routed by claims (no holder among those that answered).
    A read/info/edit/remove/exec/query/move-out of an entry nobody answering holds, while a member is
    away, is `transient_dependency` naming the away member — never `not_found`.
  - The union's root (`""`) stats as a directory; other root-level operations go to the fallback
    (else the first member).
  - Every member refusing a glob (e.g. all `permission_denied`) answers that refusal; every member
    away answers an empty listing naming them all in `unavailable`.
- **The holder lookup** globs every member's root with `*` in parallel and matches the entry on the
  listed path's last segment (as `VfsCall.NameOf` does), so any spelling a member answers in works.
  One lookup per operation, no cache.
- **Failure taxonomy:** a member answering an error whose code is retryable, or throwing (other than
  cancellation), is away; every other error (`permission_denied`, `not_found`,
  `unsupported_operation`) contributes nothing silently.
- **Beyond the ticket:** the glob tool adds a `hint` when `unavailable` is set ("say that part could not
  be read right now, never that it has nothing"), and `VfsCall.ListAsync` treats a listing with
  `unavailable` as cut short (named in `vfsTruncated`, so the daemon asks for a name the listing lacks
  instead of answering ENOENT). Search merges and caps at `maxResults` but has no `unavailable` field
  (the spec names the glob's only): a member away during a search silently contributes nothing.
- **Writes through the bridge:** text goes through `CreateAsync` (routed by content); a blob write
  (`WriteChunksAsync`) is buffered whole and routed the same way.
- The merged mount's `Name` is the members' names joined with `+` (`ha-watches+sugary-watches`); its
  description joins theirs with a space; its capabilities are the union in `FileSystemOperations.All`
  order. `ServedNames`/the prompt see one mount.

For tickets 03 and 10 (joining the union):

- Each watches backend overrides exactly two members of `FileSystemBackendBase` beyond its operations:
  `public override string MountPoint => "/watchers";` (its `FilesystemName` stays `ha-watches` /
  `sugary-watches`) and `public override FileSystemUnion? Union => …` —
  `new("/triggers/*/trigger", [], Fallback: true)` for `HaWatchFileSystem`,
  `new("/triggers/*/trigger", ["glucose", "glucose_trend", "glucose_forecast", "sensor"])` for Sugary's.
  Nothing else: no workspace, landing or shell reach, or the member is refused.
- Each member's `GlobAsync("", "*")` must list its top-level entries (`<id>/`): that listing is how the
  union finds an entry's holder. A member answering `permission_denied` to a nobody caller is fine.
- The agent needs no change; `FileSystemServerConformanceTests` rows for the two filesystems should pin
  the published `union`.

Tests run: `Tests.Unit` 7370 passed (24 new in `UnionFileSystemTests`, 10 registry, 6 discovery/resource,
2 glob tool, 1 bridge walk, 36 conformance rows at `/union`); `Tests.Integration` (no E2E/Canary/Eval/
Smoke) 2189 passed, 1 skipped (`CamoufoxFingerprintPinTests`, unrelated), including the new
`VfsBridgeOverTheWireTests.AUnionMount_IsServedToACommandAsOneDirectory`. E2E, Eval and Smoke not run.

