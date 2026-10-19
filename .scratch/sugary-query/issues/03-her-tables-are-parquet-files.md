# 03 — Her tables are Parquet files

**What to build:** The tables at `/sugary` become Parquet files — `foods.parquet`,
`periods.parquet`, `day-notes.parquet` and per month `meals/`, `items/`, `doses/` and `exercise/`
`<YYYY-MM>.parquet` — with the same layout and month rules as the CSV had. A table is not text:
`file_read` of one is refused with a hint naming `query` and the sandbox, while `README.md` stays
readable. The bytes are what the bridge and the copy tool read, so anything that reads Parquet gets
real Parquet.

**Blocked by:** 02

**Status:** done

Spec: `.scratch/sugary-query/spec.md`

- [x] The engine contract gains Parquet encoding of one table; it runs on its own connection, never runs model SQL, and is single-threaded so two renders of the same log are the same bytes (tested).
- [x] Glob lists the `.parquet` files and months exactly as it listed the CSV ones; a month with nothing logged has no file.
- [x] The byte read serves a table's Parquet bytes and `README.md` as UTF-8; Parquet read back has the schema's columns and types, including the `_local` columns.
- [x] Info reports a Parquet file's true size, equal to the length the byte read returns.
- [x] `file_read` of a `.parquet` path answers a new `FsError.QueryOnly`, whose hint names `query` and the file's path in the sandbox; `file_read` of `README.md` still answers its text; `fs_read` is still advertised.
- [x] The text search override is gone: the Sugary server no longer advertises `fs_search`, and the conformance row says so.
- [x] The mount description and `README.md` name the Parquet files, the SQL tables, each column's type and the `_local` columns.
- [x] The app's CSV export is unchanged (`SugaryExportApiTests`).

## Comments

- The MCP tests that pinned a month's CSV text now read the month's Parquet back with DuckDB (the
  fixture's `TableRowsAsync`, over `fs_blob_read`) for what a file holds, and ask `query` for what the
  log says. The CSV-only rules — the formula apostrophe and no byte-order mark — stay pinned where the
  CSV still is: the export tests and `SugaryTablesTests`. A table holds her text as she wrote it; the
  apostrophe belongs to the export alone.
- The import round trip compares the import's input with the app's export, not with the mount.
- `SearchAsync` and the search timeout went with the CSV; `query` with `ILIKE` finds a food by name.
- The skill still names the CSV paths and text search until ticket 05 rewrites it.
- `.claude/rules/virtual-filesystem.md` gains the `query` contract.
