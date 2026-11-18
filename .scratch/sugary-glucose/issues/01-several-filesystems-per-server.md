# 01: A server may serve several filesystems

**What to build:** `AddFileSystemTools<T>()` / `AddFileSystemResource<T>()` callable once per backend type on one server. One `fs_*` tool per operation any of the server's backends overrides; each call is dispatched by its `filesystem` argument to that backend (absent → the first registered filesystem, so older outposts and kit servers keep working); an operation the named backend does not override answers `unsupported_operation`. The resource publishes `operations` (the backend's overridden `FileSystemOperations` keys); discovery uses them per mount and falls back to the server's tools when absent. `McpFileSystemBackend` sends `filesystem` on every call. No behaviour change for any existing server. Spec: "Several filesystems per server".

**Blocked by:** None.

Status: ready-for-agent

- [ ] A test server with two backends advertises the union of their operations and dispatches each call by `filesystem`.
- [ ] A call naming a filesystem the server does not serve is refused, naming the ones it does.
- [ ] A call with no `filesystem` reaches the first registered backend.
- [ ] Discovery mounts each filesystem with its own published operations; a resource without `operations` keeps today's server-wide capability.
- [ ] `FileSystemServerConformanceTests` asserts per filesystem: published operations = overridden operations; server tools = their union.
- [ ] Every existing server, outpost and the suite stay green.
