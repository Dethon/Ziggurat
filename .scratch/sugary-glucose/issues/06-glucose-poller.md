# 06: Sugary polls each link and keeps the readings

**What to build:** `GlucoseReading` and `GlucoseSensor` (`IPersonal`) and migration; `GlucosePoller` (`BackgroundService`, `Sugary:Glucose:PollSeconds` default 60 in appsettings, staggered, bounded concurrency) reading `/graph` for every live link through the factory context with explicit owners. Current reading `source=current`, graph points `source=graph`, skipped within 2.5 min of an existing reading; sensor facts upserted; the link's state records last success, failure kind and since when; rate limits back off by `Retry-After`; a refused credential stops polling that link until it is saved again. A hook after each poll for the watch engine (ticket 11). Spec: "The poller".

**Blocked by:** 05

Status: ready-for-agent

- [ ] Against the fake over a fake clock: readings accumulate past twelve hours without duplicates.
- [ ] A refused login marks the link failing and stops; a 429 waits `Retry-After`; an outage is recorded as `link_failing` with its start.
- [ ] One person's failure never stops another's polling.
- [ ] Readings belong to their link's owner and no one else.
