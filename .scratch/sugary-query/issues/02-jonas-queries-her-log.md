# 02 — Jonas queries her log

**What to build:** Jonas can ask one DuckDB SQL statement of her whole log and get back only the
rows that answer it. `query` becomes a filesystem operation like the others — one entry in the one
list, a domain tool `domain__filesystem__query`, forwarded over MCP — and `/sugary` is the first
mount to offer it, over all seven tables across every month, on a fresh locked-down DuckDB per
query. The mount's files stay CSV for now.

**Blocked by:** 01

**Status:** done

Spec: `.scratch/sugary-query/spec.md`

- [x] `query` is in the one list of filesystem operations (wire tool `fs_query`, result `FsQueryResult`, tool key `filesystem.query`, capability `query`); the base answers unsupported and a backend offers it only by overriding it; the registrar, capability map, session filter and tool keys pick it up with no hand wiring.
- [x] `domain__filesystem__query` takes a path, a statement and an optional row cap; its footprint is a read of the path; it is offered wherever the filesystem feature is, and the mounts section lists `query` only on mounts that advertise it. Its description is the generic half plus the mount's `DescribeQuery`, which names the tables, the DuckDB dialect and the README.
- [x] The agent's wire proxy for a remote mount forwards `query` to `fs_query` and reads back `FsQueryResult`; `McpSugaryServerTests` runs a query against the real server over MCP.
- [x] Domain holds an engine contract (run one statement over a set of tables with a row cap and cancellation); the DuckDB.NET implementation lives in the Sugary server, at a pinned DuckDB version recorded for ticket 04.
- [x] On `/sugary`, any path inside the mount sees `meals`, `items`, `doses`, `exercise` (every month), `foods`, `periods` and `day_notes`; a lookup, a sum and a join of meals with doses on `meal_id` each answer correctly; a meal saved between two queries is in the second.
- [x] Every instant column has a sibling `<column>_local` timestamp on the home's clock, correct across a DST change.
- [x] The answer echoes the caller's path, lists columns with types, returns at most the cap (a constant default and ceiling), reports the row count and `truncated` with a suggestion to narrow when cut; values serialise as decided in the spec (instant as the CSV's offset stamp, `_local` naive ISO, date `yyyy-MM-dd`, numbers and booleans as JSON, empty as `null`).
- [x] An invalid statement answers the non-retryable invalid-input envelope with the engine's message and a hint naming the README; a statement past the time bound is interrupted and answers `timeout`.
- [x] Lockdown, each a test: `read_csv('/etc/passwd')` and reading the database file are refused, `INSTALL`/`LOAD` are refused, `SET` after the lock is refused, and a write statement leaves the next query's tables unchanged. Memory and threads are bounded.
- [x] `FileSystemServerConformanceTests` pins `fs_query` on the Sugary server; `VfsVirtualPathConformanceTests` covers the query tool with no exemption.

## Comments

- **DuckDB is pinned at 1.5.6** (`DuckDB.NET.Data.Full` 1.5.6, released 2026-09-28). Ticket 04 installs
  the same version in the sandbox: the `duckdb` Python package 1.5.6 and the v1.5.6 CLI.
- `DescribeQuery` is the wire tool's description, like every other `Describe*` hook; the agent filters
  the raw `fs_*` tools out, so the model reads the tables from the domain tool's own description, the
  mount's description in the mounts section and `README.md`. Ticket 03 rewrites the last two.
- The lockdown is fixed when the database opens (no file access, no extension install or load,
  bounded threads and memory), then the clock is set and the configuration locked before any table
  is loaded — `TimeZone` is not accepted at open. Removing the lockdown fails seven of the eight
  reach-past-the-tables cases; the eighth (`set enable_external_access = true`) DuckDB refuses on its
  own while the database runs.
- The Sugary image drops the macOS and Windows native libraries after publish: DuckDB's are a few
  hundred MB together.
