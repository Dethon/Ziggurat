# 01: A server may serve several filesystems

**What to build:** `AddFileSystemTools<T>()` / `AddFileSystemResource<T>()` callable once per backend type on one server. One `fs_*` tool per operation any of the server's backends overrides; each call is dispatched by its `filesystem` argument to that backend (absent → the first registered filesystem, so older outposts and kit servers keep working); an operation the named backend does not override answers `unsupported_operation`. The resource publishes `operations` (the backend's overridden `FileSystemOperations` keys); discovery uses them per mount and falls back to the server's tools when absent. `McpFileSystemBackend` sends `filesystem` on every call. No behaviour change for any existing server. Spec: "Several filesystems per server".

**Blocked by:** None.

Status: done

- [x] A test server with two backends advertises the union of their operations and dispatches each call by `filesystem`.
- [x] A call naming a filesystem the server does not serve is refused, naming the ones it does.
- [x] A call with no `filesystem` reaches the first registered backend.
- [x] Discovery mounts each filesystem with its own published operations; a resource without `operations` keeps today's server-wide capability.
- [x] `FileSystemServerConformanceTests` asserts per filesystem: published operations = overridden operations; server tools = their union.
- [x] Every existing server, outpost and the suite stay green.

## Comments

Done in 6a1235d5c. Deviations and things worth knowing:

- **`McpFileSystemBackend` already sent `filesystem` on every call** (`WithFilesystem` wraps every
  operation, not only the blob ones), so no production change was needed there; a new test
  (`McpFileSystemBackendFilesystemArgTests.EveryOperation_CarriesTheFilesystemName`) pins it over
  `FileSystemOperations.ToolNames`.
- **`filesystem` is read off the call's arguments, not declared on each tool**, so every server's
  `tools/list` schema is byte-identical to before. Absent, JSON null or empty all mean the first
  registered filesystem.
- **The resource body gains `operations`** (`fs_*` tool names, read off the registered type exactly as
  the tools are) — the one wire change every existing server makes. Discovery keeps only published
  operations the server also advertises as tools; an absent field falls back to the server's tools,
  an empty list means none. The per-mount set goes to the proxy too (`advertisedOperations`), so
  `Lacks`/move-out asks are per filesystem.
- **The move-out check is not `unsupported_operation`** on a filesystem with no rule: it answers
  allowed, its base default (ADR 0015). Other model-facing operations the named backend does not
  override answer through the base's own refusal (which names what the mount can do); the two blob
  operations answer an `unsupported_operation` envelope from the registrar.
- **An unknown name is refused `invalid_argument`**, the message naming the served filesystems.
- **A shared tool's description** is the first registered filesystem's *that overrides that
  operation* (identical to before for every single-filesystem server).
- Calling `AddFileSystemTools<T>()` twice for the same type throws at registration.
- `MultiFileSystemFixture` published hand-written `library`/`notes` resources over a backend named
  `vault`; a server now refuses a name it does not serve, so each vault host builds its backend
  under the name it publishes and uses `AddFileSystemResource`. No shipped server had that mismatch.
- Conformance (`FileSystemServerConformanceTests`) is per filesystem (published operations =
  overridden = `_advertised[name]`, mount capabilities from its own operations) and per server (tools
  = union over the rows sharing a server id; filesystem resources = those rows' names), so
  `ha-watches`/`sugary-watches` are one new row each.

For ticket 02: `McpFileSystemDiscovery.ReadMount(json, serverOperations)` now answers
`DiscoveredMount(FileSystemMount Mount, IReadOnlySet<string> Operations)` — the members' operations a
union's capabilities are the union of. Mounting is still per resource through
`VirtualFileSystemRegistry.TryMount`, first wins. The resource body is the anonymous object in
`FileSystemServerResource.Describe`, and discovery reads it through the private
`FileSystemResourceMetadata` record — `union` is one field on each.

For tickets 03 and 10 (the watches filesystems):

- **Registration order is the compatibility contract.** A call naming no filesystem reaches the
  first `AddFileSystemTools<T>()`, so `HaFileSystem` and `SugaryFileSystem` must stay registered
  first, with the watches filesystems after them.
- **One backend type per filesystem on a server** — a second registration of the same type throws —
  so `ha-watches` and `sugary-watches` each need a type of their own.
- **No deploy ordering is needed**: a new agent falls back to the server's tools where a resource
  publishes no `operations`, and an old agent already sends `filesystem` on every call.

Tests run: `Tests.Unit` 7288 passed; `Tests.Integration` (no E2E/Canary/Eval/Smoke) 2188 passed,
1 skipped (`CamoufoxFingerprintPinTests`, unrelated); the rest of the non-paid suite 476 passed,
4 skipped (`ResearchJudgeCalibrationTests`, eval-gated). E2E, Eval and Smoke were not run.

