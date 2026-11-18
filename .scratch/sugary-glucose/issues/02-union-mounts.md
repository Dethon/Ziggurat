# 02: A mount point may be a union of filesystems

**What to build:** A filesystem resource may declare `union` (`routeBy` JSON pointer with `*` for array items, plus `claims` or `fallback: true`). Discovery reads it onto `FileSystemMount`. The registry merges members declaring `union` at one mount point into a generic `UnionFileSystem` (Domain); a non-union collision keeps first-wins. The union binds every member to the caller, merges glob/search, sends an existing top-level entry's operations to its holder, routes a new entry's create by claims (fallback when nothing is claimed; refusal naming values when two members' claims mix), reports failed members in a defaulted `unavailable` field of the glob result, and drops a member that refuses the caller. Capabilities = union of members' operations. No watch-specific code. Spec: "Union mounts".

**Blocked by:** 01

Status: ready-for-agent

- [ ] Two in-memory members at one mount: listing merges, read/edit/remove reach the holder, a create routes by claim, an unclaimed create goes to the fallback, a mixed create is refused naming the values.
- [ ] A create of an entry another member holds goes to the holder.
- [ ] A member answering `permission_denied` adds nothing and fails nothing; a member that throws a transient error is named in `unavailable`.
- [ ] Caller binding reaches every member (a member sees the call's conversation).
- [ ] A union member declaring a workspace, shell reach or landing target is not mounted as a member (logged).
- [ ] `VfsVirtualPathConformanceTests` cover a union; the bridge serves a union mount's files to a sandbox command (`VfsBridgeOverTheWireTests`).
