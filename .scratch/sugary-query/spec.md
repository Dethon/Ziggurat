# Sugary: her log as tables to query

Status: ready-for-agent

## Problem Statement

Her carb log is served at `/sugary` as CSV text, one file per table per month, and the only way
Jonas can answer a question about it is to read those files. "When did she last eat paella" is a
text search for a name and a guess at which hit is latest; "how many grams of carbs did she average
at dinner in August" is a whole month read into the conversation and added up by the model, which is
slow, costs tokens on every row, and is arithmetic a model gets wrong. A question across tables —
the dose she took for each meal that ended high — is several reads and a join done in the model's
head. The log grows every day, so every one of these gets worse.

The sandbox could do this properly — a script reads the tables and computes — but the files are CSV
spread over months, so every script starts by parsing text and guessing types, and the sandbox has
no data tooling installed beyond `python3` and `pip`.

## Solution

The tables at `/sugary` become **Parquet** files, typed and columnar, and the mount gains a new
filesystem operation, **`query`**: Jonas asks one SQL question of the whole log and gets back only
the rows that answer it. The same Parquet files reach the sandbox through the bridge like every
other served mount, and the sandbox image carries **DuckDB** (Python package and CLI) and
**Polars**, so a command can analyse, plot or join the log with `/ha` history with real tools.

- **Ask, don't read.** `domain__filesystem__query` takes a path on a mount and one DuckDB SQL
  statement. Every table of the mount is visible to it at once (`meals`, `items`, `doses`,
  `exercise`, `foods`, `periods`, `day_notes`), each spanning every month, so a join across them is
  one call. The answer is the columns and at most a bounded number of rows, and it says when there
  were more.
- **A table is not text.** `file_read` of a `.parquet` file is refused with a hint that names
  `query` and the sandbox. `README.md` stays readable: it explains every column, and is still read
  before a number is trusted.
- **The same files in a command.** `/sugary/meals/*.parquet` is real Parquet to a command in the
  sandbox: `duckdb -c "select … from '/sugary/meals/*.parquet'"` and `pl.scan_parquet(…)` both work,
  and the SQL is the same dialect the `query` tool speaks, because it is the same engine at the same
  version.
- **Nothing about her log changes.** It is still read-only, still rendered from Sugary's database on
  every read so always current, and the app's CSV export is byte for byte what it was.

## User Stories

1. As Fran, I want to ask Jonas when she last ate a food and get the right date, so that I don't
   have to scroll through the Sugary app myself.
2. As Fran, I want to ask Jonas for totals and averages over her log ("carbs per day last week",
   "how many meals ended high in August"), so that the answer is computed rather than estimated by
   the model.
3. As Fran, I want to ask how much insulin she took for a meal, so that I can see her record next
   to the meal's carbs and outcome without anyone suggesting a dose.
4. As Fran, I want questions across her meals, doses and exercise answered in one step, so that
   a question like "which meals ended low after a walk" doesn't take a dozen tool calls.
5. As Fran, I want Jonas's answers about her log to be fast and cheap, so that asking about it
   doesn't fill the conversation with a month of rows.
6. As Fran, I want to ask about her periods and day notes the same way as her meals, so that
   every table of her log works alike.
7. As Fran, I want Jonas to produce a chart or a deeper analysis of her log in the sandbox, so that
   I can see trends a single query cannot show.
8. As Fran, I want Jonas to line her log up against her glucose history from `/ha` in the sandbox,
   so that a meal and the readings after it can be examined together.
9. As Jonas, I want one tool that answers a question with SQL over every table of the mount, so
   that I never read a whole month to find one row.
10. As Jonas, I want the `query` tool's description to name the tables and the SQL dialect, so that
    I write a statement that runs on the first try.
11. As Jonas, I want the mount's README to explain every column, its unit and its type, so that I
    read a number correctly before I report it.
12. As Jonas, I want a query whose answer is longer than the row cap to say it was cut short and how
    many rows there were, so that I narrow it rather than report a partial answer as whole.
13. As Jonas, I want a SQL error to come back as a non-retryable envelope with the engine's message
    and a hint, so that I fix the statement rather than resend it.
14. As Jonas, I want a query that runs too long to be stopped and answered as a timeout, so that a
    bad statement cannot hang the turn.
15. As Jonas, I want `file_read` of a Parquet file to tell me to use `query` or the sandbox instead,
    so that a refusal points at what works.
16. As Jonas, I want glob to keep listing her months under `meals/`, `items/`, `doses/` and
    `exercise/`, so that I can still see which months she logged.
17. As Jonas, I want every time column both as an instant and as the time on the home's clock, so
    that "Tuesday's dinner" is a plain filter and a meal still lines up with `/ha` history.
18. As Jonas, I want `query` to answer from her log as it is now, so that a meal saved a moment ago
    is in the next answer.
19. As Jonas, I want `query` to need no approval, so that reading her log works the way reading any
    other file of hers does.
20. As Jonas, I want a command in the sandbox to read `/sugary` Parquet with DuckDB or Polars
    without installing anything, so that analysis is one command, not a setup.
21. As Jonas, I want the `duckdb` CLI in the sandbox to speak exactly the SQL the `query` tool
    speaks, so that a statement moves between the two unchanged.
22. As Jonas, I want the sugary-log skill to teach `query` first and the sandbox for analysis, so
    that I choose the cheap path for a lookup and the sandbox for real work.
23. As Jonas, I want the skill's rule against suggesting doses unchanged, so that a computed answer
    never becomes dosing advice.
24. As her, I want my log to stay read-only to the agent, so that nothing Jonas runs can change what
    I recorded.
25. As her, I want the app's CSV export to stay exactly as it is, so that what I import elsewhere
    keeps working.
26. As the deployment's owner, I want a query unable to read any file on the Sugary server — its
    database, its secrets — so that SQL is not a way out of the mount.
27. As the deployment's owner, I want a query unable to load or download extensions, so that a
    statement cannot change what the engine can do.
28. As the deployment's owner, I want a query bounded in memory, threads and time, so that one
    statement cannot starve the Sugary server that also serves her app.
29. As the deployment's owner, I want each query to run on a fresh engine that is thrown away, so
    that no statement can affect the next one.
30. As the deployment's owner, I want `query` to be one more operation in the one list of
    filesystem operations, so that the registrar, the capability map, the session filter and the
    tool feature pick it up without hand wiring.
31. As the deployment's owner, I want a mount to offer `query` only by overriding it, so that no
    mount advertises a query it cannot answer.
32. As the deployment's owner, I want the conformance tests to cover `query` like every other
    operation, so that a path in its answer is always virtual.
33. As the deployment's owner, I want the DuckDB engine pinned to one version in the server and in
    the sandbox image, so that the two never disagree about SQL or Parquet.
34. As the deployment's owner, I want the DuckDB native library to ship only in the image that uses
    it, so that every other server stays the size it is.
35. As a developer adding another mount later, I want the table model and the engine contract to
    be generic, so that a second mount can offer `query` by describing its tables, not by
    re-implementing an engine.
36. As a developer, I want a Parquet file to be byte-identical across two renders of the same log,
    so that the size a stat reports is the size a read returns.
37. As a developer, I want the table schema defined once and encoded three ways (CSV, Parquet, SQL),
    so that the export, the files and the query cannot drift apart.
38. As a developer, I want the eval to expect `query` in the two Jonas scenarios about her log, so
    that the behaviour this spec wants is the behaviour that goes green.

## Implementation Decisions

**A new filesystem operation, `query`.**
- Added to the one list of filesystem operations: wire tool `fs_query`, backend method `QueryAsync`
  taking a path, a SQL statement and a row cap, result type `FsQueryResult`, tool key
  `filesystem.query`, capability `query`. The base class answers unsupported; a backend declares the
  capability by overriding it. The registrar, the result-type table, the discovery capability map,
  the session's filter set and the feature's tool keys all derive from the list — nothing is wired by
  hand.
- A new domain tool, `domain__filesystem__query`, resolves the path through the registry and calls
  the backend, like the other `Vfs*` tools. Its call footprint is a read of the path. It is offered
  wherever the filesystem feature is, like every other filesystem tool; the mounts section lists
  `query` among a mount's operations only where the mount advertises it. Jonas's `filesystem`
  feature and his `domain__filesystem*` whitelist mean he gets it, unasked, with no config change.
- The backend gains a `DescribeQuery` hook, the tool description's mount-specific half: the tables
  it exposes, that the dialect is DuckDB SQL, and where its columns are explained.
- The path names the mount; any path inside the mount resolves to the same set of tables, which are
  named in the backend's own words, not as file paths. Cross-mount queries do not exist: a tool
  cannot reach across mounts, and the sandbox is the place to join `/sugary` with `/ha`.
- `FsQueryResult`: the caller's path echoed back, the columns (name and type), the rows as arrays of
  JSON values, the number of rows the statement produced, `truncated` and, when truncated, a
  suggestion to narrow the statement. The row cap has a default and a ceiling, both constants rather
  than settings, as the walk budgets are.
- The one list does not reach the agent's wire proxy for a remote mount (`McpFileSystemBackend`),
  which forwards each operation by hand: it gains `QueryAsync`, calling `fs_query` and reading back
  `FsQueryResult`. The sandbox E2E holds its backend in process and cannot catch the proxy's
  absence; a proxy unit test and `McpSugaryServerTests` (the real server over MCP) do.
- A row's values serialise one way, decided here rather than left to the engine's string forms: an
  instant as the same offset-carrying stamp on the home's clock the CSV uses (so a `query` answer
  and `/ha` history read on one clock), a `_local` timestamp as a naive ISO timestamp, a date as
  `yyyy-MM-dd`, a decimal or integer as a JSON number, a boolean as a JSON boolean, an empty cell as
  `null`.
- A statement the engine rejects answers the invalid-input code with the engine's message and a hint
  naming the README; one that exceeds the time bound answers `timeout`. Both go through `FsResult`
  and `FsError` like every filesystem failure.

**A generic table model and an engine contract in Domain.**
- A typed table model: a table has a name, ordered columns each with a type (text, integer, decimal,
  boolean, instant, home-clock timestamp, date) and rows. It is generic, not Sugary's.
- An engine contract with two operations: encode one table as Parquet bytes, and run one statement
  over a set of tables with a row cap and a cancellation. Domain holds the contract; the
  implementation, on DuckDB.NET, lives in the Sugary server — the only mount that needs it today —
  so the native library ships in that image alone. It moves to Infrastructure when a second mount
  needs it.
- One DuckDB version is pinned, and the sandbox image's Python package and CLI use the same one.

**Sugary's tables, defined once.**
- Sugary's table renderer becomes a typed schema per table, encoded three ways: CSV (unchanged, byte
  for byte — the app's export, its import and its rules depend on it), Parquet (the mount's files)
  and SQL (the engine's tables). The README documents that one schema.
- Every instant column (`eaten_at`, `taken_at`, `started_at`, `ended_at`) is an instant, plus a
  sibling `<column>_local` timestamp on the home's clock, so a local-day question is a plain filter
  and a row still lines up with `/ha` history. Calendar days (`start_date`, `end_date`, `date`) are
  dates.

**The mount's files.**
- `README.md`, `foods.parquet`, `periods.parquet`, `day-notes.parquet`, and per month
  `meals/<YYYY-MM>.parquet`, `items/<YYYY-MM>.parquet`, `doses/<YYYY-MM>.parquet`,
  `exercise/<YYYY-MM>.parquet`: the same layout and the same month rules as today, with the
  extension changed. The SQL tables are `meals`, `items`, `doses`, `exercise` (every month),
  `foods`, `periods` and `day_notes`.
- The text read refuses a `.parquet` path with a new `FsError.QueryOnly`, whose hint names `query`
  and the file's path in the sandbox — the `ExecutableOnly` pattern. Capability stays per operation:
  `fs_read` is still advertised and still serves `README.md`. Do not drop the text read override:
  the bridge serves a command's read only where `file_read` is offered.
- The byte read (`ReadChunksAsync`) serves the Parquet bytes, and the README as UTF-8. It is what
  the bridge reads first, so a command gets real Parquet; the copy tool can also copy a table into
  the sandbox's home.
- Info reports a Parquet file's true size, rendering it to measure. Polars and DuckDB seek to the
  footer, so an unknown or zero size breaks them. Encoding is single-threaded and deterministic, so
  two renders of the same log are the same bytes.
- The text search override is removed: its files are binary, and `query` with `ILIKE` replaces
  "find a food by name". Glob and info stay.

**The engine's lockdown, per query.**
- A fresh in-memory database per query, thrown away afterwards. The tables are loaded into it, then
  external access is turned off, extension auto-install and auto-load are turned off, a memory limit
  and thread count are set, and the configuration is locked — all before the statement runs. The
  statement runs under a time bound and is interrupted at it.
- Parquet encoding runs on its own connection, before any of that, since writing a file needs file
  access; it never runs SQL from the model.
- Several statements in one call are not refused: the database is a throwaway, so a write changes
  nothing that lasts.

**The sandbox image.**
- Pinned `polars` and `duckdb` installed with `pip install --break-system-packages`, into the system
  site-packages, so they work under the minimal `PATH` a command gets.
- The pinned `duckdb` CLI binary, the same DuckDB version, on the image's `PATH`.
- `/sugary` is already a served mount; no AppArmor or bridge change.

**The skill and the README.**
- The sugary-log skill is rewritten around `query`: the tables and what each holds, `README.md`
  before a number, `query` for every lookup and sum, the sandbox with DuckDB or Polars over
  `/sugary/**/*.parquet` for analysis, charts and joins with `/ha`. The rule against suggesting a
  dose is kept word for word.
- The mount's description and README name the Parquet files, the SQL tables, the column types and
  the `_local` columns.

## Testing Decisions

A good test here drives what a caller sees — a tool's envelope, a command's output — and never the
engine or the renderer's internals. Four seams, three of them already there:

- **The domain filesystem tools over a registry holding the real `SugaryFileSystem`**, with the real
  DuckDB engine and an in-memory log: no Docker. This is where most of the behaviour is pinned —
  `query` answers (a lookup, a sum, a join of meals and doses, the row cap and `truncated`, an
  invalid statement, a timeout, a meal saved between two queries), the `QueryOnly` refusal and its
  hint, `README.md` still read as text, glob listing `.parquet` months, info reporting the size the
  byte read returns, two renders being identical, `_local` columns on the home's clock across a DST
  change, and the lockdown (`read_csv('/etc/passwd')`, reading the database file, `INSTALL`/`LOAD`
  and `SET` after the lock are all refused). Prior art: `SugaryFileSystemTests` and the `Vfs*Tool`
  unit tests.
- **`SandboxMountsE2ETests`**: the real sandbox image, launcher and FUSE daemon, with a
  `SugaryFileSystem` in the in-memory registry. A command runs
  `python3 -c "import polars as pl; print(pl.read_parquet('/sugary/meals/<month>.parquet'))"` and
  `duckdb -c "select … from '/sugary/meals/*.parquet'"` and gets the rows. Nothing else proves the
  size and footer path, so it is not skipped. Prior art: `JqOnARenderedStatusFile_ReadsItWhole`.
- **The existing conformance tests**: `FileSystemServerConformanceTests` pins that the Sugary server
  advertises `fs_query`, no longer advertises `fs_search`, and that its advertised tools, overrides
  and capabilities are one set; `VfsVirtualPathConformanceTests` drives `query` through its
  translation because its tool set comes from the one list. `SugaryExportApiTests` pins the CSV
  export unchanged.
- **The eval**: the two Jonas scenarios about her log (`HerLogIsReadThroughTheSkill`,
  `HerDosesAreReadNeverAdvisedOn`) expect a `query` call after the skill loads instead of a read
  under `/sugary/`, and the eval's "looking" permission includes `query`. The path argument now
  names only the mount, so the doses scenario's evidence that her doses were consulted moves to the
  `sql` argument naming `doses` — the existing argument matchers are path-shaped, so one that
  matches a text argument is added. The trigger claim's wording ("before `/sugary` is read") becomes
  "queried". The prompt snapshot tests
  are updated for the skill, the mount description and the new tool. A changed-tier run against
  `master` after the skill edit.

Red first throughout, in this order: the operation and its tool with the conformance tests; the
typed schema with the CSV pinned; Parquet files, `QueryOnly` and true sizes; the engine and the
`query` override with the lockdown; the sandbox image with its E2E; the skill, README and eval.

## Out of Scope

- Translating between SQL dialects. DuckDB SQL is the one dialect; a future source joins by DuckDB
  attaching it, not by translating SQL. No SQL parser is added.
- A second mount offering `query`, or moving the engine to Infrastructure before one does.
- Queries across mounts from the tool. The sandbox is where `/sugary` meets `/ha`.
- Any write to her log through the mount, the tool or the sandbox.
- Changing the app's CSV export or import.
- Collapsing the per-month files into one file per table.
- Charting or analysis tools of the agent's own beyond what the sandbox image provides.

## Further Notes

- Build `base-sdk` explicitly after the Domain changes before `up --build`; a stale base image hides
  them.
- The Sugary server's image grows by the DuckDB native library; the sandbox image grows by DuckDB
  and Polars. Both are expected.
- Confirm on the first armed run that Jonas reaches for `query` rather than globbing and reading;
  the refusal hint is the backstop, the skill is the lever.
