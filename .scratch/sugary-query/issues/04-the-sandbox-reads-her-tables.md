# 04 — The sandbox reads her tables

**What to build:** A command in the sandbox can analyse her log with real tools, with nothing to
install: the image carries pinned Polars, the `duckdb` Python package and the `duckdb` CLI, at the
same DuckDB version the Sugary server's engine uses, so a statement moves between the `query` tool
and the shell unchanged. `/sugary` is already a served mount, so its Parquet files reach the command
through the bridge like any other.

**Blocked by:** 03

**Status:** ready-for-agent

Spec: `.scratch/sugary-query/spec.md`

- [ ] The sandbox image installs pinned `polars` and `duckdb` with `pip install --break-system-packages`, importable under the minimal environment a command gets.
- [ ] The pinned `duckdb` CLI binary for the image's architecture is on the command's `PATH`, at the DuckDB version ticket 02 pinned.
- [ ] In `SandboxMountsE2ETests`, with a `SugaryFileSystem` holding a few meals in the in-memory registry, `python3 -c "import polars as pl; print(pl.read_parquet('/sugary/meals/<month>.parquet'))"` prints those meals.
- [ ] In the same test class, `duckdb -c "select … from '/sugary/meals/*.parquet'"` returns the same rows.
- [ ] No AppArmor, bridge or launcher change was needed (if one was, the ticket says why).
