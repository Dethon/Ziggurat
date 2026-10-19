# 03 — Her tables are Parquet files

**What to build:** The tables at `/sugary` become Parquet files — `foods.parquet`,
`periods.parquet`, `day-notes.parquet` and per month `meals/`, `items/`, `doses/` and `exercise/`
`<YYYY-MM>.parquet` — with the same layout and month rules as the CSV had. A table is not text:
`file_read` of one is refused with a hint naming `query` and the sandbox, while `README.md` stays
readable. The bytes are what the bridge and the copy tool read, so anything that reads Parquet gets
real Parquet.

**Blocked by:** 02

**Status:** ready-for-agent

Spec: `.scratch/sugary-query/spec.md`

- [ ] The engine contract gains Parquet encoding of one table; it runs on its own connection, never runs model SQL, and is single-threaded so two renders of the same log are the same bytes (tested).
- [ ] Glob lists the `.parquet` files and months exactly as it listed the CSV ones; a month with nothing logged has no file.
- [ ] The byte read serves a table's Parquet bytes and `README.md` as UTF-8; Parquet read back has the schema's columns and types, including the `_local` columns.
- [ ] Info reports a Parquet file's true size, equal to the length the byte read returns.
- [ ] `file_read` of a `.parquet` path answers a new `FsError.QueryOnly`, whose hint names `query` and the file's path in the sandbox; `file_read` of `README.md` still answers its text; `fs_read` is still advertised.
- [ ] The text search override is gone: the Sugary server no longer advertises `fs_search`, and the conformance row says so.
- [ ] The mount description and `README.md` name the Parquet files, the SQL tables, each column's type and the `_local` columns.
- [ ] The app's CSV export is unchanged (`SugaryExportApiTests`).
