# 02 — Jonas queries her log

**What to build:** Jonas can ask one DuckDB SQL statement of her whole log and get back only the
rows that answer it. `query` becomes a filesystem operation like the others — one entry in the one
list, a domain tool `domain__filesystem__query`, forwarded over MCP — and `/sugary` is the first
mount to offer it, over all seven tables across every month, on a fresh locked-down DuckDB per
query. The mount's files stay CSV for now.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `.scratch/sugary-query/spec.md`

- [ ] `query` is in the one list of filesystem operations (wire tool `fs_query`, result `FsQueryResult`, tool key `filesystem.query`, capability `query`); the base answers unsupported and a backend offers it only by overriding it; the registrar, capability map, session filter and tool keys pick it up with no hand wiring.
- [ ] `domain__filesystem__query` takes a path, a statement and an optional row cap; its footprint is a read of the path; it is offered wherever the filesystem feature is, and the mounts section lists `query` only on mounts that advertise it. Its description is the generic half plus the mount's `DescribeQuery`, which names the tables, the DuckDB dialect and the README.
- [ ] The agent's wire proxy for a remote mount forwards `query` to `fs_query` and reads back `FsQueryResult`; `McpSugaryServerTests` runs a query against the real server over MCP.
- [ ] Domain holds an engine contract (run one statement over a set of tables with a row cap and cancellation); the DuckDB.NET implementation lives in the Sugary server, at a pinned DuckDB version recorded for ticket 04.
- [ ] On `/sugary`, any path inside the mount sees `meals`, `items`, `doses`, `exercise` (every month), `foods`, `periods` and `day_notes`; a lookup, a sum and a join of meals with doses on `meal_id` each answer correctly; a meal saved between two queries is in the second.
- [ ] Every instant column has a sibling `<column>_local` timestamp on the home's clock, correct across a DST change.
- [ ] The answer echoes the caller's path, lists columns with types, returns at most the cap (a constant default and ceiling), reports the row count and `truncated` with a suggestion to narrow when cut; values serialise as decided in the spec (instant as the CSV's offset stamp, `_local` naive ISO, date `yyyy-MM-dd`, numbers and booleans as JSON, empty as `null`).
- [ ] An invalid statement answers the non-retryable invalid-input envelope with the engine's message and a hint naming the README; a statement past the time bound is interrupted and answers `timeout`.
- [ ] Lockdown, each a test: `read_csv('/etc/passwd')` and reading the database file are refused, `INSTALL`/`LOAD` are refused, `SET` after the lock is refused, and a write statement leaves the next query's tables unchanged. Memory and threads are bounded.
- [ ] `FileSystemServerConformanceTests` pins `fs_query` on the Sugary server; `VfsVirtualPathConformanceTests` covers the query tool with no exemption.
