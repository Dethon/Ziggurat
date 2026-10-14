# 01 — Her standalone log readable at /sugary

**What to build:** A new Sugary dual-role server runs in its own container with a SQLite store of meals, the foods in each meal and saved foods (the relational model of the standalone app). A one-off import reads the standalone app's three CSV exports (meals, items, foods) and writes them in, keeping identifiers, times and sources. The server serves a read-only `/sugary` mount rendered from the database on every read: `meals/<YYYY-MM>.csv`, `items/<YYYY-MM>.csv`, `foods.csv` and a `README.md` explaining every column. An agent given the mount can list, search and read her real log. See the spec's "The /sugary mount" and "The Sugary server".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] The server is one new row in the server table and passes the server contract tests (settings singleton, host, one error filter, the `/mcp` gate).
- [x] An ADR records SQLite as this server's store and why it departs from Redis.
- [x] Importing the standalone exports twice does not duplicate anything.
- [x] Each month with meals has a meals file and an items file; a month with none has no file.
- [x] Columns, RFC 4180 quoting, UTF-8 without a byte-order mark and the escaping of formula-leading text match the standalone export.
- [x] Every time carries its offset in the home's zone, a server setting defaulting to the zone the Home Assistant mount uses.
- [x] The README explains every column, including that carbs exclude fibre and that the first estimate covers only items whose source is `estimate`.
- [x] The backend overrides read, glob, info and text search and nothing that writes; the filesystem conformance row passes (no workspace, not a landing target, no shell reach).
- [x] A write, move or delete under `/sugary` is refused.
- [x] A change in the database shows on the next read, with nothing written to disk.
- [x] Tests drive the server through MCP (resource, file tools), like the scheduling server's integration tests.

## Comments

**Built (2026-10-14).** `McpServerSugary`, a new server in its own container (`mcp-sugary`, port
6017), storing her log in SQLite at `/data/sugary.db` in its own volume (`./volumes/sugary`):

- **Store.** The standalone app's relational model ported as it was (`McpServerSugary/Store`: meals
  owning their items, saved foods apart, times as UTC text), through EF Core with migrations from
  the first version, applied when the host starts. EF and the SQLite provider live in the server
  project, not in `Infrastructure`, so no other container carries them. Domain sees the log only
  through `ISugaryLog`. ADR: `docs/adr/0043-sugarys-log-is-sqlite-in-its-own-volume.md`.
- **Mount.** `SugaryFileSystem` (`Domain/Tools/Sugary/Vfs`) overrides read, info, glob and search
  only, and renders `README.md`, `foods.csv`, `meals/<YYYY-MM>.csv` and `items/<YYYY-MM>.csv` from
  the database on every read. The CSV rules are ported from the standalone `Csv.cs` and
  `ExportEndpoints.cs` (`Domain/Tools/Sugary/SugaryCsv.cs`, `SugaryTables.cs`), and the standalone
  export tests are carried over line for line, read through MCP.
- **Import.** A verb, not a startup step: `docker exec mcp-sugary dotnet McpServerSugary.dll import
  /data/import`, reading `sugary-meals.csv`, `sugary-items.csv` and `sugary-foods.csv` (the names the
  app's downloads have). It is written in the compose service's comment. It runs in one transaction,
  and a meal or food whose id is already in the database is left as it is, so a second run adds
  nothing. A cell it cannot read imports nothing and names the file and line.
- **Tests.** `Tests/Integration/McpServerTests/McpSugaryServerTests.cs` and `SugaryImportTests.cs`
  boot the real module on a temporary SQLite file per test (`Tests/Integration/Fixtures/
  SugaryServer.cs`) and drive it over MCP. `Tests/Unit/Domain/Sugary/SugaryFileSystemTests.cs` checks
  the refusal of a write, move or delete. There is a new row in `McpServerRegistrations` and one in
  `FileSystemServerConformanceTests`.

**Decisions to know about.**

- **Why import is a verb.** If the import ran on startup and the files stayed in the volume, every
  restart would bring back a meal she had since deleted (once ticket 02 lets her delete).
- **The home's zone.** The `HomeZone` setting (IANA id) defaults to empty, which means the
  container's own zone. Compose gives the container the same `TZ` anchor (`Europe/Madrid`) as the
  Home Assistant container, so the default is "the zone the Home Assistant mount uses" without the
  server calling Home Assistant. An unknown id fails startup.
- **A month is the home's month.** A meal is filed under the month its `eaten_at` shows in the home's
  zone, not the UTC month: 23:30 UTC on 31 March is in April's file in Madrid.
- **Defused text is restored on import.** The export writes `=x` as `'=x`, so on import an
  apostrophe followed by a formula character is removed. Text that really began with `'=` loses
  its apostrophe.
- **Two import edge cases.** A row in `sugary-items.csv` whose `meal_id` is not in
  `sugary-meals.csv` is dropped without being counted, because an item cannot stand without its
  meal. If an id appears twice in one file, the first row is kept.
- **What the export does not carry is not invented.** A food's creation time is not in the
  downloads, so imported foods get the time of the import. Meal photos are not in the export either,
  so imported meals have none. Numbers come back at the export's two decimal places.
- **A read with offset/limit returns raw lines.** The CSV is never line-numbered, so what is read is
  the file exactly as written.

**Deferred.**

- The server's row is `McpServerRole.Tool` for now. It becomes dual-role when the channel arrives
  (tickets 03/04).
- No agent mounts `/sugary` yet. The endpoint is not in `Agent/appsettings.json`: the spec gives
  the server to the `sugary` agent (ticket 04), and adding it to Jonas now would be a decision of its
  own.
- No prompt or skill is served yet (spec: later tickets), and there is no HTTP API or PWA (ticket 02).
- No secret is needed yet, so `DockerCompose/.env` is unchanged. The app's access code arrives with
  ticket 02.

