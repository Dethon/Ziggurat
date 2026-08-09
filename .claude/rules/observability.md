---
paths:
  - "Observability/**"
  - "Dashboard.Client/**"
  - "Infrastructure/Metrics/**"
  - "Domain/DTOs/Metrics/**"
  - "Domain/Metrics/**"
---

# Observability Architecture

## Publishing

Two roles. A **metrics publisher** (`IMetricsPublisher`, one `void Publish(MetricEvent)`) is what a caller holds: it cannot fail, cannot block and cannot be observed, so no call site has to decide what a failed publish means. A **metric sink** (`IMetricSink`, `Task SendAsync`, may throw) is the transport behind it; `RedisMetricSink` is its one adapter and lives in `Infrastructure/Metrics/` because Domain never consumes a sink. `BufferedMetricsPublisher` is the only publisher a host registers: publishing writes to a bounded channel (drop-on-full, logged), and a background reader drains into the sink, logging whatever it refuses. `docs/adr/0002-metrics-publishing-is-fire-and-forget.md` records why the interface is not awaitable — do not "fix" it back into a `Task`.

Becoming a metrics-publishing host is **one call**: `services.AddMetricsPublishing(serviceName)` registers the sink, the buffered publisher and the `HeartbeatService` together, so a host cannot resolve a bare sink as its caller-facing publisher and cannot publish without appearing on the health roster. `Tests/Integration/Metrics/MetricsRegistrationContractTests.cs` boots each host's real registration module and asserts both.

Measuring a span is a **scope**, not a stopwatch triple: `publisher.MeasureLatency(stage, conversationId, agentId, model)` (`Domain/Metrics/LatencyScope.cs`) publishes its `LatencyEvent` on disposal, covering the return path and the throw path from one statement, and exposes `ElapsedMilliseconds` for a site that also emits a domain-specific event carrying the same duration. It publishes on an early return too, so open it *after* any guard that can return before the measured work begins.

Optional publisher parameters coalesce once to `NoOpMetricsPublisher.Instance` where the publisher is stored, so no type null-checks before publishing.

## Collection

Published events reach the Redis Pub/Sub channel `metrics:events`. `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`); `MetricsQueryService` serves grouped aggregations by dimension/metric enum. The dashboard is hybrid: REST for history on page load, SignalR for live updates, `LocalStorageService` for UI state.

## The dashboard's live connection

`MetricsLiveConnection` owns being live, and it is the only thing that does. Becoming live is one
ordered sequence inside it: bind the handlers to the hub connection, start it retrying until it
succeeds, publish the status, then catch up. Steps three and four also run when the transport
reconnects on its own. The layout calls connect and catches nothing, because the module does not
fail — it keeps trying.

- **The seam is `IMetricsHubConnection`**, one generic receive verb keyed by wire method name plus
  the three lifecycle events. A twelfth server push is a line in the binder, not a member on the
  interface, the implementation and the fake. Never hand-write a named registration method.
- **`MetricsRetryPolicy` is the one schedule**: zero, two, ten, thirty seconds, then thirty
  forever, and it never returns the value that means stop. It drives both automatic reconnection and
  the module's own first-start loop, which delays through the injected `TimeProvider`. Automatic
  reconnection has never covered the first attempt, so replacing only the policy would leave a
  dashboard opened during a deploy just as dead as before.
- **The started latch records a start that succeeded**, never one that was attempted.
- **`ConnectionStore` is the only source of connection status**: connecting, live or reconnecting,
  with no permanent disconnected state, because the module never gives up. The page-load path does
  not report a failed request as a lost connection. The indicator lives in the layout, so every page
  shows it; the overview reads the same store.
- **`MetricsCatchUp` walks the family table** for the range each family already holds, so a
  recovery does not move the user's group-by, metric or time choices. It is awaited as the last step
  of becoming live. On the **first** epoch it is normally skipped, because the ordinary page load
  fetches the same data and catching up too would double every request on first paint. A first load
  that **failed** gets the catch-up anyway — otherwise a dashboard opened during an outage shows a
  green dot over empty pages until the user reloads. The load may still be in flight when the
  decision is taken, so the module waits for that first load to settle and catches up if it failed.
  Later epochs always catch up. A failure inside the catch-up is logged and leaves the connection
  live.
- **A page load stamps only the families that page draws.** `DataLoadEffect.LoadAsync` is given
  them — one family for every breakdown page, `MetricFamilyTable.OverviewFamilies` (the four behind
  the activity feed plus voice) for the Overview. Every family is still reloaded, each over the
  range its own page chose, which is what makes the catch-up promise above true: stamping all seven
  meant a push or a catch-up for a family re-read it over whatever page the user was on.
- **Only the latest load writes.** A family fetches its events and hands back the store write, which
  `MetricFamily.LoadEventsAsync` runs only while no later load has started; `OverviewFigures` stamps
  its summary read the same way. Two quick time-pill clicks overlap, the thirty-day responses are
  the slower ones, and an event list has nothing that re-reads it the way the refresh coalescer
  brings a breakdown back into line.
- **An append is bounded.** Every store writes its event lists through `EventWindow.Append`, so a tab
  left open cannot grow them without limit. A load writes whatever the range answered with; an
  append keeps the list at the length the load left it, or grows it to `EventWindow.Cap`, whichever
  is longer.
- **The catch-up holds pushes while it runs.** `MetricsHubBinder.HoldPushes` queues incoming pushes,
  and `ReleaseHeldPushesAsync` replays them once the snapshot has landed. Without the hold, a
  snapshot fetched before a push arrived would erase that push, and a push the snapshot already
  contains would land twice. Any change to the catch-up must keep both ends of that pairing.
- `MetricsHubBinder` is otherwise the binder and nothing else: the mapping from a push to a store
  update and a family refresh, with `Bind` and `Unbind` driven by the module.

Health tiles come from `ServiceHealthRegistry`, a sorted-set roster (`metrics:health:seen`) scored by *last registration*, not last health — reachability is the separate TTL'd `metrics:health:<service>` key. Services publishing `HeartbeatEvent`s register themselves; third-party containers are registered by `HttpHealthProbeService`, which polls the URLs in `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response, even non-2xx, as up. A probe target re-registers every cycle whether or not it answers, so a down service stays visible as a red tile, while a retired one stops registering and ages off after `Retention` (7 days).
