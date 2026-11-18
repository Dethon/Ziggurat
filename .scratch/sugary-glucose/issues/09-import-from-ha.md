# 09: Import a person's glucose from Home Assistant

**What to build:** `SugaryCommand import-glucose --ha <url> --token <file> --entity <id> --tenant <tid> --user <uid> [--days N]`: recorder history in ten-day windows (`minimal_response`, numeric states only), then hourly long-term statistics means (WebSocket `recorder/statistics_during_period`) for days before the history; `source=import` / `import-hourly`; skip within 2.5 min of an existing reading; print counts. Imported readings change no watch state. Spec: "Import from Home Assistant".

**Blocked by:** 06

Status: done

- [x] Against `FakeHomeAssistant`/the HA test container: a run imports, a second run imports nothing.
- [x] Non-numeric states (`unavailable`) are skipped; stamps are UTC instants.
- [x] The token is read from a file, never an argument value, and never logged.

## Comments

What was built:

- `McpServerSugary/Import/GlucoseImport.cs`: `GlucoseImport.RunAsync(contexts, IHomeAssistantClient, GlucoseImportRequest(Person, Entity, Days), TimeProvider, ct)` -> `GlucoseImportReport(Imported, Hourly, Skipped, NotReadings)`.
  - Reads the entity's `unit_of_measurement` (`GET /api/states/<entity>`): `mg/dL` as is, `mmol/L` × 18.016 (`MgPerDlPerMmol`); any other unit, or none, refuses the run (exit 1, nothing written). A missing entity refuses too.
  - History from `now - days` to now (now truncated to the second) in ten-day windows (`Window`), asked one after another as UTC strings with `Z` (`yyyy-MM-ddTHH:mm:ssZ`; a naive one is read in HA's zone), through `ListHistoryAsync` (`minimal_response&no_attributes`). Only a positive, finite number is a reading; `unavailable`, `unknown`, empty, `NaN`, negatives are counted as "not readings".
  - Statistics: `ListStatisticsAsync(entity, from, historyFirst, "hour")`, keeping the rows wholly before the history's first point (`Start >= from && End <= historyFirst`, filtered here too because HA's answer is not trusted to honour the window), a null mean skipped. With no history at all, the whole span is statistics.
  - One transaction through the factory context (`SugaryDatabase.MigrateAsync` first), the person's existing instants loaded once with `IgnoreQueryFilters()` and the owner spelled out; every row written with `UserId`/`TenantId` set explicitly, `Trend = Unknown`, `At` UTC.
- `McpServerSugary/Import/GlucoseImportCommand.cs`: the verb's flags, the token file, the client, the report line. `SugaryCommand.IsCommand` now also matches `import-glucose` (otherwise the server would have started), and `SugaryCommand.RunAsync` takes an optional `TimeProvider` for the tests.
- `Tests/Integration/Fixtures/FakeHomeAssistantSocket.cs`: REST `GET /api/states/{id}` (`States`) and `GET /api/history/period/{start}` (`History`, `HistoryRequests`) on the same address as its WebSocket, behind the same bearer token. The history read behaves as HA's does: the state at the window's start stamped at the start, then the changes after it up to `end_time`. Additive; the calendar/statistics/registry tests and the eval's fake home are unchanged.
- `Tests/Integration/McpServerTests/SugaryGlucoseImportTests.cs` (20): a run imports as hers (not his, not `legacy`) and a second run imports nothing; non-numeric states skipped; mmol/L converted (history and hourly means); a `%` sensor and a sensor with no unit refused; unknown entity refused; the exact ten-day windows asked and a window's opening state not imported (a sensor silent across two window starts); spacing against her readings and thinning among the imported; hourly means only for whole hours before the history, none for an hour she has readings in, none without a mean, idempotent; no history -> hourly means for the whole span; `--token` refused and its value never echoed; a refused token imports nothing and is not printed; empty/`legacy` user and bad `--days` refused; instants UTC.
- `.claude/rules/sugary.md`: the command's paragraph, and the import added to the `IgnoreQueryFilters()` list. `DockerCompose/docker-compose.yml`: the invocation beside the `import` one.
- Runs: the new suite 18/18; `Tests/Unit` + Sugary integration + HA socket client tests + `FakeHomeAssistantTests` + `DeploymentConformance`: 7908 passed, 0 failed.

Decisions and deviations:

- **Client: Infrastructure's `HomeAssistantClient`, reused.** `McpServerSugary` already references Infrastructure (Sugary's `AddSugaryStore`, `Mcp.Hosting` beside it), and the layer rules forbid only Domain reaching down and `Mcp.Hosting` referencing Infrastructure on a server's behalf. The contract `IHomeAssistantClient.ListHistoryAsync`/`ListStatisticsAsync`/`GetStateAsync` is exactly what the import needs, and it is the code the HA mount's `history`/`statistics` run, so a second, smaller client would only duplicate the WebSocket handshake. The command gives it a two-minute HTTP and WebSocket timeout (90 days of hourly statistics is one frame).
- **`--token-file <path>`, not the ticket's `--token <file>`** (the brief's choice): `--token` is refused whatever follows it (exit 2, the value never echoed), so a token typed there never reaches HA either. No line the command prints carries an argument's value besides the entity and the URL (in HA errors).
- **A window's opening element is no reading.** HA answers each history window with the state at its start stamped at the start (`HomeAssistantClientTests.ListHistoryAsync_FirstElement_…`); a point at or before a window's start is dropped, so a sensor silent across a boundary does not grow a stale value there, and the oldest window never imports the value from before the span.
- **Spacing like the curve** (ticket 06's closing line): a history point is kept only when no reading of hers, nor one this run just kept, is strictly within 2.5 minutes; walked oldest first, so a per-minute HA history is thinned to about one reading every 2.5–5 minutes. An hourly mean is skipped when she has any reading in `[Start, End)` or within the spacing of its stamp.
- **An hourly mean is stamped at its hour's start** (HA's row `Start`), source `import-hourly`. Ticket 07's README should say "an `import-hourly` value is the mean of the hour starting at `read_at`".
- **`--days` bounds the whole lookback**: history from `now - days`, statistics for the part of that span before the history's first point. Default 90 (prod's `purge_keep_days`).
- **Counts:** "imported N readings and M hourly means …; skipped K already there … and U that were not readings". Skipped counts history points and hourly means; a null-mean hour counts nowhere.
- **Imported readings never reach the watch engine, structurally:** the command builds a container with `AddSugaryStore` alone (no poller, no `IGlucosePollObserver`). For ticket 11: if the engine ever reads readings from the table on a clock pass (rather than what a poll hands it), it must leave out `import`/`import-hourly` rows, which can be minutes old when HA's history reaches now.
- **Not guarded:** the tenant/user ids are not checked against the accounts service (the command has no call token); a typo writes rows nobody sees. Look the ids up first (below). A poll writing for her during the import cannot make a duplicate key unless LibreLinkUp's and HA's instants coincide to the tick.

Prod (ai370), run once (checked read-only on 2026-11-18: the kit's `.env` has `LAN_BIND_ADDRESS=192.168.5.45`, so HA's 8123 is published on the LAN and `mcp-sugary` opened a TCP connection to `192.168.5.45:8123`; the database is `volumes/sugary/sugary.db` (`DatabasePath` `/data/sugary.db`); `PUID`/`PGID` are 1000, the ssh user's uid; the host has no `sqlite3`):

```sh
cd ~/ziggurat/docker-compose
# HA long-lived token (HA profile -> Security -> Long-lived access tokens), into the volume, owned by
# the ssh user (uid 1000 = PUID, which the container runs as) and readable by it alone:
install -m 600 /dev/stdin volumes/sugary/ha-token     # paste the token, Enter, Ctrl-D
# her user id (32 hex): her account on the household's members page (or the dashboard's operator view)
# the glucose entity: HA's Developer tools -> States, the LibreLink glucose sensor (unit mg/dL)
docker exec mcp-sugary dotnet McpServerSugary.dll import-glucose \
  --ha http://192.168.5.45:8123 --token-file /data/ha-token \
  --entity sensor.<her glucose sensor> \
  --tenant 3b538b13ba740cf45cb7f712bf66339d --user <her user id>
rm volumes/sugary/ha-token
```

The home household is `3b538b13ba740cf45cb7f712bf66339d`. Run it once the image with this commit is deployed; a rerun is harmless.

Follow-ups:

- Ticket 07: Home Assistant's recorder keeps a row when the state changes, so a flat stretch can be one imported point where LibreLinkUp would have had several; coverage and time-in-range SQL should be checked against `import` rows (and `import-hourly` is one row per hour by design).
- `--token-file`'s path is never printed either (a token pasted there instead would be), pinned by `ATokenFileThatCannotBeRead_IsNotNamed`; a rerun days later, with every window boundary moved, imports nothing (`ARerunDaysLater_ImportsNothingNew`). Suite now 20.

Red first: the suite was written before the command and was red (14 of 18; the four argument-refusal cases passed against the old usage branch). Mutations each turned their test red: keeping a window's opening state, dropping the thinning among imported points, skipping the mmol conversion, and letting statistics rows from the history's span in.
