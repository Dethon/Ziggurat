# 11: Evaluating glucose triggers

**What to build:** `GlucoseTriggers` — a pure evaluation of a watch's triggers and conditions over recent readings, link state, sensor facts, persisted `GlucoseWatchState` and the clock, answering new state and which triggers fired. Threshold, trend, forecast and sensor states as the spec defines; `for`; crossing-only; `once`; time conditions on the household's clock. Called after each poll for that person; only on a newest reading under 10 min old; state persisted. A watch written while its condition holds starts as holding-and-fired. Spec: "Triggers", "Evaluation".

**Blocked by:** 06, 10

Status: done

- [x] Table-driven tests per trigger kind: crossing, holding without re-firing, leaving and re-crossing, `for` interrupted.
- [x] `no_readings` fires on a silent sensor and never on a link failure; `link_failing` the reverse.
- [x] Back-filled and imported readings change nothing; a restart (state reloaded) neither re-fires nor loses a pending `for`.

## Comments

What was built:

- **`GlucoseTriggers`** (`Domain/Tools/Sugary/Watches/GlucoseTriggers.cs`, pure):
  `Judge(GlucoseWatchSpec watch, IReadOnlyList<GlucoseTriggerCondition> prior, GlucoseTriggerFacts facts) -> GlucoseJudgement(States, Fired)`.
  `GlucoseTriggerFacts`: `Now`, `Zone`, `Occasion` (`Poll` | `Clock`), `Recent` (her own readings of the last
  `Lookback` = 30 min, oldest first), `Newest` (her newest own reading, however old), `Link`
  (`GlucoseLinkHealth(LinkedAt, LastSuccessAt, FailingSince)`, null without a link), `SensorActivatedAt`.
  States reuse ticket 10's `GlucoseTriggerCondition`, one per trigger judged now or before, in order.
- **The fire report** (`GlucoseTriggerFire`, Domain):

  ```csharp
  public sealed record GlucoseTriggerFire(
      int TriggerIndex, GlucoseTrigger Trigger, DateTimeOffset At, SensorReading? Newest,
      double? MgPerDlPerMinute, double? ForecastMgPerDl, DateTimeOffset? Since, DateTimeOffset? SensorEndsAt);
  ```

  `At` is the newest reading's instant for a reading trigger, the clock's for a sensor trigger; `Newest`
  carries value and arrow; the rate is the fit's slope (trend and forecast), the forecast the projected value;
  `Since` when the condition began (first reading in it; `FailingSince`; the last reading for `no_readings`).
- **The effects seam** (`McpServerSugary/Glucose/GlucoseWatchEffects.cs`), for tickets 12 and 13:

  ```csharp
  public sealed record FiredGlucoseWatch(Account Subject, StoredGlucoseWatch Watch, GlucoseWatchSpec Spec);
  public sealed record GlucoseEffectsOutcome(int Delivered) { bool AnyDelivered; static None; }
  public interface IGlucoseWatchEffects
  {
      Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct);
  }
  ```

  `Subject.TenantId` is the household (`StoredGlucoseWatch` has none). Registered as
  `LoggedGlucoseWatchEffects`, a log line answering `None`; replace that registration in `ConfigModule`.
- **`GlucoseWatchEngine`** (`McpServerSugary/Glucose/`) is the `IGlucosePollObserver` (one singleton, the
  observer resolved from it; `NoGlucoseWatches` is gone) and has `TickAsync`, which `GlucoseWatchLoop`
  (`BackgroundService`, `PeriodicTimer` of one minute on `TimeProvider`, registered beside `GlucosePollLoop`
  only with a sealing key) calls for every subject with an armed watch. Factory context with
  `IgnoreQueryFilters()` (added to `.claude/rules/sugary.md`'s list, with a paragraph on the engine).
- **Tests:** `Tests/Unit/Domain/Sugary/Watches/GlucoseTriggersTests.cs` (27: table-driven level, trend and
  forecast series — crossing, holding, leaving and re-crossing, `for` held and interrupted, the boundary —
  first judgement per trigger, stale newest, the clock leaving reading triggers alone, conditions incl.
  across midnight and weekday, `no_readings` vs link failure, `link_failing`, `ending` and a new sensor, the
  report's fields) on `FakeTimeProvider`; `Tests/Integration/McpServerTests/SugaryGlucoseWatchEngineTests.cs`
  (19, a real Sugary store: a back-filled curve point not judged, fires and `LastFiredAt`, written-while-holding, restart through fresh engine
  instances (pending `for` kept, no re-fire), imports neither a trend nor a newest reading, disabled/spent
  untouched, `once` spent only when delivered, throwing effects contained, a watch rewritten (other spec,
  same spec) or removed during its fire not resurrected, poll and clock at once fire once, `link_failing` on
  the clock for a refused link, `ending` on the latest-started sensor last seen, the saved phone zone, and
  the poller against `FakeLibreLinkUp` handing a fall below and an outage to the watches, `no_readings`
  never firing on the outage).

Decisions:

- **Zone: the subject's phones' saved zone, else the server's** (`HomeCalendar.OnSaved`, now shared with
  `PenReminders.ClockOf`). A watch has no proof of its household until ticket 12 stores the delegation
  (`StoredGlucoseWatch.Delegation` is null), and `CallHousehold.OfFire` needs that; once it is there the
  engine can read the zone through `CallHousehold.OfFire(...).ZoneAsync(calendar.Zone, ct)` as
  `ScheduleDispatcherService` does. No migration.
- **Conditions gate the fire, not the episode**: state is tracked whatever the window, and an episode
  holding and not yet fired fires when the window opens (a low from 21:50 fires at 22:00 under
  `after: 22:00`). Home Assistant would have dropped it; for a health alarm a low still going is worth
  telling. Pinned by `ATimeCondition_GatesTheFire_NotTheEpisode`. The condition is read at `Now`.
- **First judgement is per trigger, not per watch**: a trigger with no state row is judged without
  firing, recorded fired where it holds — a sensor trigger's row can be written by the clock before any
  reading trigger is judged. A trigger that cannot be judged (stale newest, fewer than 3/4 readings or no
  time spread, no link, no sensor, `no_readings` while the link fails) keeps its row, or gets none, never a
  "not holding" that would make the next reading a crossing.
- **Which kinds when:** on a poll, every trigger (reading triggers only on a newest under 10 min old); on
  the clock, sensor triggers only. The `for` of a level trigger runs on reading instants.
- **`no_readings` is judged on LibreLinkUp's last answer** (`LastSuccessAt - newest >= for`, newest falling
  back to `LinkedAt`), not on the clock, so a server that asked nobody (down, password not opening) never
  calls her sensor silent; while `FailingSince` is set it is not judged. `link_failing` holds while
  `Now - FailingSince >= for`; `ending` at activation + 14 days (`GlucoseTriggers.SensorLifetime`; the
  product type is not mapped to a lifetime). The newest reading is the later of the poll's `Newest` and her
  newest stored `current`/`graph` reading; `import`/`import-hourly` never count.
- **Thresholds are strict** (`below: 70` does not hold at 70), no hysteresis, as Home Assistant's
  `numeric_state`.
- **Effects first, then one guarded save**: states, `LastFiredAt` (on any fire, delivered or not), and for a
  `once` watch whose fire reached somebody `Spent = true, Enabled = false`, in one transaction that first
  updates the watch where `Spec`, `Enabled` and `!Spent` are as read and then requires the watch's state-row
  count to be what was read (a rewrite deletes them, so this catches a rewrite to the same words); either
  failing writes nothing. A crash between effects and save could fire an episode again after restart — the
  price of saving `Spent` from the effects' answer in the same write. Unchanged states and no fire write
  nothing. A `once` watch whose triggers fire together calls effects once.
- **One judgement per person at a time** (a semaphore per subject in the engine), so a poll and the clock's
  pass cannot both read an episode as unfired. The effects run inside it and inside the poller's slot.
- A spec that no longer parses is logged and skipped.
- **Back-filled curve points never trigger a judgement** (only the poll's newest reading is judged; pinned by
  `ABackFilledCurvePoint_IsNotJudged`), but `graph` points inside the window do inform the trend's and the
  forecast's fit, since they are her readings.
- **Disabled and spent are the engine's query** (`Enabled && !Spent` on the columns), not `GlucoseTriggers`'s,
  which takes no flag.
- **Effects run inside the per-person lock and the poller's concurrency slot**: tickets 12 and 13 should hand a
  fire on (enqueue a turn, start a ring) rather than await an agent's whole turn there.

Red first, for the pure function only: `GlucoseTriggersTests` was written before `GlucoseTriggers` and was red
on compile, then passed whole. The engine, its effects seam and its loop were written before
`SugaryGlucoseWatchEngineTests`; that suite's first run was red on 13 of 18 from a real bug (a `once && effects`
short-circuit that skipped the effects of every watch that was not `once`), fixed, and then every guard was
proven by mutation. Mutations each turned their test red: firing on a first judgement,
recording "not holding" for an unjudgeable trigger, widening freshness, judging `no_readings` on the clock,
ignoring conditions, a per-call semaphore (two fires), dropping the row-count guard (same-spec rewrite
resurrected), counting imports, any sensor instead of the last seen, the server's zone instead of the phone's,
and not containing a throwing effect. The `Spec` guard alone survives its mutation (the count guard catches
every rewrite that can race a fire); kept for a rewrite racing a first judgement.
