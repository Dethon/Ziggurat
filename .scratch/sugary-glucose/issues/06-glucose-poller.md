# 06: Sugary polls each link and keeps the readings

**What to build:** `GlucoseReading` and `GlucoseSensor` (`IPersonal`) and migration; `GlucosePoller` (`BackgroundService`, `Sugary:Glucose:PollSeconds` default 60 in appsettings, staggered, bounded concurrency) reading `/graph` for every live link through the factory context with explicit owners. Current reading `source=current`, graph points `source=graph`, skipped within 2.5 min of an existing reading; sensor facts upserted; the link's state records last success, failure kind and since when; rate limits back off by `Retry-After`; a refused credential stops polling that link until it is saved again. A hook after each poll for the watch engine (ticket 11). Spec: "The poller".

**Blocked by:** 05

Status: done

- [x] Against the fake over a fake clock: readings accumulate past twelve hours without duplicates.
- [x] A refused login marks the link failing and stops; a 429 waits `Retry-After`; an outage is recorded as `link_failing` with its start.
- [x] One person's failure never stops another's polling.
- [x] Readings belong to their link's owner and no one else.

## Comments

What was built:

- `McpServerSugary/Store/GlucoseReading.cs`: `GlucoseReading` (`IPersonal`, key `(UserId, At)`): `At` (UTC instant, LibreLinkUp's `FactoryTimestamp`, stored as UTC text), `MgPerDl` (double), `Trend` (`Domain.DTOs.Sugary.GlucoseTrend`, text), `Source` (`GlucoseSource.Current|Graph|Import|ImportHourly`, stored as `current|graph|import|import-hourly`). `GlucoseSensor` (`IPersonal`, key `(UserId, Serial)`): `Serial`, `ActivatedAt`, `ProductType` (int?, LibreLinkUp's `pt`), `LastSeenAt` (the newest is the one on her now). `GlucoseReadingRules.Spacing` (2.5 min) and the source words. No key from either into the link. Migration `20261118011051_GlucoseReadings` (class renamed `GlucoseReadings`), on top of `GlucoseLinks`; the model diff is the two tables.
- `McpServerSugary/Glucose/GlucosePoller.cs`: `PollDueAsync(ct)` reads every live link (`PatientId != null`, `LastFailure != CredentialsRefused`) through the factory context with `IgnoreQueryFilters()`, polls those whose moment has come and are not being polled already, at most 4 at a time (`MaxConcurrentPolls`, a constant), and returns when they are done. Each link's moment is an offset into the period fixed by a SHA-256 of its owner's id (staggered, stable across restarts); a link seen for the first time (or linked again) is polled at its next moment, so at most once per period. One `GraphAsync` per poll.
- `McpServerSugary/Glucose/GlucosePollLoop.cs`: a `BackgroundService` ticking every 5 s (`Tick`) on `TimeProvider`, starting a sweep without waiting for the last, so a slow answer holds only its own link. Registered only when `SealingKey` is set.
- `McpServerSugary/Glucose/GlucosePoll.cs`: the seam for ticket 11 (below), and `NoGlucoseWatches`, the no-op registered for it.
- Settings: `SugarySettings.Glucose` (`GlucoseSettings.PollSeconds`, default 60), bound at the root like every Sugary setting: `Glucose:PollSeconds` in `appsettings.json` (env `GLUCOSE__POLLSECONDS`), not `Sugary:Glucose:PollSeconds`.
- `FakeLibreLinkUp.Connection`: `Down` (that connection's graph answers 503) and `BeforeGraph` (runs while the graph request is out), so per-person outages and the re-link race are testable when people are polled at once.
- `.claude/rules/sugary.md`: the two tables, the poller added to the `IgnoreQueryFilters()` list, and a paragraph on the poller.
- Tests: `Tests/Integration/McpServerTests/SugaryGlucosePollerTests.cs` (18) and `Unlinking_ForgetsTheLinkAndLeavesHerLog` now proves her readings and sensors survive unlinking (ticket 05's open box). Sugary integration + `Tests/Unit` (including `MigrationsMatchTheModelTests`): 7884 passed, 0 failed; server table and conformance tests: 264 passed.

The observer seam (ticket 11 replaces the `NoGlucoseWatches` registration):

```csharp
public interface IGlucosePollObserver
{
    Task PolledAsync(GlucosePoll poll, CancellationToken ct);
}

public sealed record GlucosePoll(
    Account Person, IReadOnlyList<SensorReading> Stored, GlucoseLinkState Link, SensorReading? Newest);

public sealed record GlucoseLinkState(
    DateTimeOffset LinkedAt, DateTimeOffset? LastSuccessAt, GlucoseLinkFailure? LastFailure,
    string? FailureDetail, DateTimeOffset? FailingSince);
```

Decisions and deviations:

- **Any reading within 2.5 minutes of one she has is skipped**, not only graph points, and the current reading is considered first in each poll, so it (with its arrow) is the one kept for its minute. At a poll a minute, most current readings are therefore not kept (about one every three minutes is), which still gives the trend trigger's 3-in-15 and the forecast's 4-in-20. A curve point can be stood in for by a reading up to 2.5 minutes away, so consecutive kept readings may be a little over 5 minutes apart; the test pins that every moment the curve showed is kept or has a reading within the spacing.
- **Readings, sensors and the link's state are one transaction, guarded** by `LinkedAt` and `PatientId` both still being what the poll read: `PUT /link/connection` changes `PatientId` without touching `LinkedAt`, and a poll out across it would otherwise keep the other person's readings as hers. A poll that loses the race writes nothing and tells the observer nothing.
- **Backoff:** `CredentialsRefused` stops the link (it leaves the live set until a re-link clears it). `RateLimited` waits `Retry-After`, or 5 minutes when absent. `Unreachable`, `StepRequired` and `VersionTooOld` wait one period, then doubling up to 15 minutes, for as long as they last (a step or an old version logs in on every ask otherwise). `FailingSince` is written as `FailingSince ?? now` in SQL, so it is the run's first failure whatever the backoff. The first answer clears all of it. The backoff is this process's: a restart asks each link at its next moment again.
- **`FailureDetail` is only a step's component or a minimum version**; `GET /link` answers it. An `Unreachable` reason goes to the log only, once per outage (a warning at the first failure, an information line at recovery).
- **A password that does not open** (no key, a changed key) skips the link without writing anything, logged once per link and process; LibreLinkUp was never asked, so it is not a link failure.
- **Curve points are kept with `Trend = Unknown`** whatever the wire parsed: only the current reading's arrow says where she was heading.
- **`Newest`** is the connection's current reading from this answer (else the curve's last point), kept or not; null after a failure. `Stored` is what this poll kept, oldest first.

For ticket 11 (the watch engine):

- Replace the `IGlucosePollObserver` registration (`ConfigModule`, `AddSingleton<IGlucosePollObserver, NoGlucoseWatches>`). The observer is called after the poll's transaction commits, inside the poller's concurrency slot, for the polled person only. An exception is logged and contained to that person.
- It is called after failures too, with `Link.LastFailure`/`FailingSince` as written, so `link_failing` can be judged on `FailingSince`. But **during backoff it is called only when the link is asked again**: up to 15 minutes apart for an outage, `Retry-After` for a rate limit, and never for a refused password. A `link_failing` with a `for` must not rely on a call landing near `FailingSince + for`; the engine needs its own clock pass, or must accept that lateness.
- `no_readings` ("LibreLinkUp answered and the newest reading is older than `for`"): on a successful poll `Link.LastFailure` is null and `Newest.At` is the newest reading the service had.
- `ending`: LibreLinkUp may list several active sensors, and one poll stamps them all with the same `LastSeenAt`, so the one on her is the latest `ActivatedAt` among those last seen.
- A new link is first polled at its own moment of the period, up to `PollSeconds` after linking, so ticket 08's current reading starts empty for up to a minute. Backoff lives in memory, so a restart does not honour a pending `Retry-After`; it asks each link at its next moment.

Red first: the tests were written before the poller and were red on compile. The first run was green except the twelve-hour test, whose own "no gap over five minutes" expectation was wrong (see above), so each guard was proven by mutation. Each of these turned its test red: dropping the `LinkedAt`/`PatientId` guard, comparing instants for equality instead of the spacing, polling refused links, ignoring `Retry-After`, overwriting `FailingSince`, not backing off, polling a link on first sight, stamping a reading with another owner, letting one person's failure escape the per-link catch, zeroing the stagger offset, raising the concurrency bound, and awaiting each sweep in the loop (a slow link then held everyone's polls). An inner catch around the observer survived its mutation, because the per-link catch already contains it, so it was removed.

Fix (after review): the "any reading within 2.5 minutes is skipped" deviation above is reverted to the spec's rule. Every `current` reading is kept (one at an instant already stored is not inserted again: a sensor that has not measured since gives the same one); only a `graph` point within `GlucoseReadingRules.Spacing` of a reading she has, or of this poll's current one, is skipped, so the curve fills gaps (before the link, a phone out of reach) and adds nothing beside the currents. At a poll a minute the log now keeps a reading a minute. Tests: `APollAMinute_KeepsEveryCurrentReading_AndNoCurvePointBesideOne` and the twelve-hour test (every current shown is kept) were red under the old rule; `CurvePointsFillingAGap_AreKept` pins the gap. The import (ticket 09) skips like the curve.
