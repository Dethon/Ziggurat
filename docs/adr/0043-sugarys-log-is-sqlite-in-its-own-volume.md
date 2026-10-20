# 0043 — Sugary's log is SQLite in its own volume

Status: accepted
Date: 2026-10-14

## Context

Sugary is the carb-counting app a person with type 1 diabetes uses to count every meal, and it is
moving into Ziggurat as a server of its own (`McpServerSugary`, `.scratch/sugary/spec.md`). It
keeps three tables: the meals she saved, the foods in each meal, and the foods she keeps on her own
list. The standalone app already stores them in SQLite through EF Core, one row per meal and one per
item, with times stored as UTC text so they sort and can be read by eye.

Every other server store in Ziggurat is Redis: schedules, download routing, conversations, memory,
the attachment and image stores. Redis is right for them. They are found by key, they expire, and
several containers share one instance. A new store would normally be Redis too, and keeping one
kind of store has real value: one backup, one container to run, one kind of test fixture.

Sugary's log is a different kind of data:

- **It is relational and read by range.** The `/sugary` mount renders a month at a time, and the
  app (ticket 02) lists, edits and exports meals by date. In Redis that needs a sorted set of meal
  ids by time, a hash or JSON value per meal, and code that keeps the two in step. A range query
  over an index does the same in one line, and the standalone app's queries already do it.
- **It is rendered as tables.** The mount's files, the app's downloads and what her care team reads
  are all CSV, one row per record. In a relational store that is a plain projection of the rows. In
  Redis every value would have to be flattened by hand.
- **It never expires and belongs to one person.** It is a health record kept for good. Nothing else
  reads or writes it, so sharing an instance with the rest of the deployment gains nothing.
- **The model and its queries already exist.** Porting the standalone `SugaryDbContext` keeps its
  schema, its time conversion and its queries. Rewriting them for Redis means re-proving all of
  them on data with health consequences.

## Decision

**The Sugary server keeps its log in a SQLite file in its own volume (`./volumes/sugary` mounted at
`/data`), through EF Core and the standalone app's relational model. It is the only server whose
store is not Redis.**

- **The database is the only copy.** The `/sugary` mount renders its CSV files from the database
  on every read, and nothing is written to disk to serve them. A later format (Parquet, say) is
  another rendering of the same rows, never a second store.
- **EF Core and the SQLite provider stay inside `McpServerSugary`.** They are not added to
  `Infrastructure`, which every server references, so no other container carries them. Domain sees
  the log through `ISugaryLog` alone (`Domain/Contracts`), and the mount (`SugaryFileSystem`) is a
  Domain backend like the others.
- **The schema is managed by EF migrations from the first version** (`McpServerSugary/Store/
  Migrations`), applied when the host starts and before the import command runs. Her real log is in
  the file from the first deployment, so its first schema change has to be a migration, not a
  rebuild of the file.
- **Each read opens its own context** (`IDbContextFactory`), because the mount is a singleton
  answering calls that overlap. The provider puts the file in write-ahead-log mode, so a reader
  never waits for the import command or, later, for the app saving a meal.
- **The standalone log arrives through an explicit verb**, `import <directory>`, which reads the
  three CSV downloads once and keeps their ids, times and sources. It is not run on startup: an
  import repeated on every start would bring back a meal she had since deleted for as long as the
  files stayed in the volume.

## Considered options

**Redis, like everything else.** One kind of store and one backup. Rejected for the reasons above:
range reads and table rendering need an index kept by hand, and the standalone app's model would
have to be rewritten and tested again. What one kind of store would save is small next to that, for
a log with one reader and one writer.

**A relational server (Postgres) in its own container.** It would give the same relational model
with a server that could be shared later. Rejected because one person's log does not need a
database server. That would be one more container to run, upgrade and back up, for data that fits
easily in a single file.

**Keep the database in the standalone app and have the server call its API.** It would mean no
port at all. Rejected by the spec: the app moves into Ziggurat so that its model calls go through
the agent, and the log has to be readable at `/sugary` with nothing outside the deployment
involved.

**CSV files on disk as the store.** The mount would serve them as they are. Rejected because the
app edits single meals and portions, which a file per month would make a rewrite of the month. The
files would also become a second copy that can go stale, which is what rendering on every read
avoids.

## Consequences

- **Backup is the volume.** Her log is in `./volumes/sugary/sugary.db` (with its `-wal` and `-shm`
  files beside it while the server runs), not in the Redis dump. A backup of the deployment has to
  include that directory. Copying the file while the server is running needs SQLite's backup or
  `VACUUM INTO`, not a plain `cp`.
- **The volume is written by the container's user.** `PUID`/`PGID` must own `./volumes/sugary`, as
  for the voice hub's volumes.
- **Tests run on real SQLite files, not testcontainers.** Each test boots the server on its own
  temporary database (`Tests/Integration/Fixtures/SugaryServer.cs`). That is faster than the Redis
  pool and needs no Docker.
- **A schema change is a migration.** It is generated with `dotnet ef migrations add <Name>
  --project McpServerSugary --output-dir Store/Migrations` (the design-time factory beside the
  context means the tool never boots the server), and is applied on the next start.
- **One server now differs from the others in how it stores data.** Anyone reading
  `McpServerSugary` should start with this record, not assume the Redis conventions in
  `.claude/rules/`. Nothing about the server table, the `/mcp` gate or the filesystem contracts
  changes: those are about how a server is hosted, not how it stores data.
