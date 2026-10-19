# 04 — The sandbox reads her tables

**What to build:** A command in the sandbox can analyse her log with real tools, with nothing to
install: the image carries pinned Polars, the `duckdb` Python package and the `duckdb` CLI, at the
same DuckDB version the Sugary server's engine uses, so a statement moves between the `query` tool
and the shell unchanged. `/sugary` is already a served mount, so its Parquet files reach the command
through the bridge like any other.

**Blocked by:** 03

**Status:** done

Spec: `.scratch/sugary-query/spec.md`

- [x] The sandbox image installs pinned `polars` and `duckdb` with `pip install --break-system-packages`, importable under the minimal environment a command gets.
- [x] The pinned `duckdb` CLI binary for the image's architecture is on the command's `PATH`, at the DuckDB version ticket 02 pinned.
- [x] In `SandboxMountsE2ETests`, with a `SugaryFileSystem` holding a few meals in the in-memory registry, `python3 -c "import polars as pl; print(pl.read_parquet('/sugary/meals/<month>.parquet'))"` prints those meals.
- [x] In the same test class, `duckdb -c "select … from '/sugary/meals/*.parquet'"` returns the same rows.
- [x] No AppArmor, bridge or launcher change was needed (if one was, the ticket says why).

## Comments

- Pinned: `duckdb==1.5.6` and `polars==1.44.2` (the last 1.x, 2026-09-09; 2.0.0 was a week old), and
  the v1.5.6 CLI for the build's architecture (`TARGETARCH`, amd64 when unset).
- No AppArmor, bridge or launcher change: `/sugary` was already served, and DuckDB's glob over
  `/sugary/meals/*.parquet` lists the directory through FUSE like `ls` does.
- The sandbox skill's preinstalled tooling and the sandbox mount's description now name DuckDB and
  Polars; the sandbox skill is in the changed-tier run ticket 05 makes.
