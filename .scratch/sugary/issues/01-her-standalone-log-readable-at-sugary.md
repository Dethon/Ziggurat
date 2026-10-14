# 01 — Her standalone log readable at /sugary

**What to build:** A new Sugary dual-role server runs in its own container with a SQLite store of meals, the foods in each meal and saved foods (the relational model of the standalone app). A one-off import reads the standalone app's three CSV exports (meals, items, foods) and writes them in, keeping identifiers, times and sources. The server serves a read-only `/sugary` mount rendered from the database on every read: `meals/<YYYY-MM>.csv`, `items/<YYYY-MM>.csv`, `foods.csv` and a `README.md` explaining every column. An agent given the mount can list, search and read her real log. See the spec's "The /sugary mount" and "The Sugary server".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The server is one new row in the server table and passes the server contract tests (settings singleton, host, one error filter, the `/mcp` gate).
- [ ] An ADR records SQLite as this server's store and why it departs from Redis.
- [ ] Importing the standalone exports twice does not duplicate anything.
- [ ] Each month with meals has a meals file and an items file; a month with none has no file.
- [ ] Columns, RFC 4180 quoting, UTF-8 without a byte-order mark and the escaping of formula-leading text match the standalone export.
- [ ] Every time carries its offset in the home's zone, a server setting defaulting to the zone the Home Assistant mount uses.
- [ ] The README explains every column, including that carbs exclude fibre and that the first estimate covers only items whose source is `estimate`.
- [ ] The backend overrides read, glob, info and text search and nothing that writes; the filesystem conformance row passes (no workspace, not a landing target, no shell reach).
- [ ] A write, move or delete under `/sugary` is refused.
- [ ] A change in the database shows on the next read, with nothing written to disk.
- [ ] Tests drive the server through MCP (resource, file tools), like the scheduling server's integration tests.
